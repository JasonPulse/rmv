#!/usr/bin/env bash
# Drives every page of the running site and fails on any status outside the
# expected one, or on any unhandled exception in the log.
#
# Exists because the alternative was one-off `docker run` containers wired to the
# compose network by hand, and one of them silently lost DNS for the database. A
# page answering 500 because my test rig was broken looks exactly like a page
# answering 500 because I broke it.
#
#   tools/smoke.sh                      # the compose stack, in Development
#   tools/smoke.sh https://www.resultsmayvary.org
#   KEEP=1 tools/smoke.sh               # leave the stack up to poke at
#
# With no argument it brings the compose stack up in Development first, so the
# admin pages are reachable without Discord, and tears it down afterwards:
# containers, volumes, and the locally built image. Nothing is left running.
# Against a remote host the admin pages are expected to redirect to sign-in.
set -euo pipefail
cd "$(dirname "$0")/.."

BASE="${1:-http://localhost:5080}"
LOCAL=0

teardown() {
    [ "$LOCAL" = 1 ] || return 0
    [ -n "${KEEP:-}" ] && { echo "KEEP set, leaving the stack up"; return 0; }
    echo "tearing down"
    # Scoped to this project and this one tag. `compose down` only touches the
    # rmv-dev project's own containers and volumes, and the image is removed by
    # exact tag. No prune, ever: there are other people's images on this machine.
    docker compose down -v --remove-orphans >/dev/null 2>&1 || true

    # Then anything else running from the image we built, by exact ancestor.
    #
    # --remove-orphans does not catch these. The image is built by compose, so
    # compose's own project labels are baked into the image and inherited by any
    # container started from it with plain `docker run`. A stray therefore carries
    # the labels without compose owning it, and one survived for a day looking
    # like it belonged to the stack.
    strays=$(docker ps -aq --filter "ancestor=rmv-web:local" 2>/dev/null || true)
    if [ -n "$strays" ]; then
        echo "  removing $(echo "$strays" | wc -l | tr -d ' ') stray container(s) from rmv-web:local"
        # kill, not stop: the app does not handle SIGTERM as PID 1, so stop waits
        # out its full timeout and looks hung.
        docker kill $strays >/dev/null 2>&1 || true
        docker rm -f $strays >/dev/null 2>&1 || true
    fi

    docker image rm -f rmv-web:local >/dev/null 2>&1 || true

    # And the untagged builds behind it. Every rebuild takes the rmv-web:local tag
    # off the previous image, which then sits as a 273MB <none> forever. Removing
    # the tag does not remove those.
    #
    # Filtered on the label compose stamps into the image it builds, so this only
    # ever matches images from this project. A bare `image prune` would take other
    # people's dangling layers with it.
    dangling=$(docker images -q --filter 'dangling=true' \
        --filter 'label=com.docker.compose.project=rmv-dev' 2>/dev/null || true)
    if [ -n "$dangling" ]; then
        echo "  removing $(echo "$dangling" | wc -l | tr -d ' ') untagged build(s) of this project"
        docker image rm -f $dangling >/dev/null 2>&1 || true
    fi
}

# On the way out however we leave, including a failed route, so a broken run does
# not leave containers and a database volume behind.
trap teardown EXIT

if [ "$BASE" = "http://localhost:5080" ]; then
    LOCAL=1
    ASPNETCORE_ENVIRONMENT=Development docker compose up -d --build >/dev/null
    # Two consecutive passes, not one. /healthz/ready depends on Postgres, and on
    # a cold volume the migration runs in a background service, so readiness can
    # flick to 200 and back to 503 while that finishes. Waiting for a single 200
    # let the route pass start mid-migration and report a 503 that was mine.
    printf 'waiting for %s ' "$BASE"
    streak=0
    for _ in $(seq 1 60); do
        if [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$BASE/healthz/ready" || true)" = "200" ]; then
            streak=$((streak + 1))
            [ "$streak" -ge 2 ] && break
        else
            streak=0
        fi
        printf '.'; sleep 2
    done
    echo
fi

# One member with one character, so /roster/{id} renders a real page rather than
# 404. Needed because teardown wipes the volume, which is the point: the previous
# version of this script checked /roster/1 and passed only because an earlier run
# had left data behind. A check that depends on yesterday's leftovers is not a
# check.
ROSTER_ID=""
if [ "$LOCAL" = 1 ]; then
    psql() { docker compose exec -T db psql -qtAX -U "${POSTGRES_USER:-rmv}" -d "${POSTGRES_DB:-rmv}" "$@"; }
    psql -c "
        insert into members (\"DiscordId\",\"DisplayName\",\"Alias\",\"Status\",\"IsAdmin\",\"FirstSeenAt\",\"LastSeenAt\")
        values ('smoke-1','smoketest_x9','Smoke','Approved',false,now(),now())
        on conflict (\"DiscordId\") do nothing;
        insert into characters (\"MemberId\",\"GamePresenceId\",\"Name\",\"Class\",\"Level\",\"Source\",\"AddedAt\")
        select m.\"Id\", g.\"Id\", 'Smoketest', 'Champion', 50, 'Manual', now()
        from members m, game_presences g
        where m.\"DiscordId\" = 'smoke-1'
        order by g.\"Id\" limit 1
        on conflict do nothing;
    " >/dev/null
    ROSTER_ID=$(psql -c "select \"Id\" from members where \"DiscordId\" = 'smoke-1'" | tr -d '[:space:]')
    echo "seeded member $ROSTER_ID with one character"
fi

# path expected_local expected_remote
ROUTES="
/ 200 200
/history 200 200
/tools 200 200
/tools/daoc/roll-parser 200 200
/tools/daoc 200 200
/tools/daoc/spellcraft 302 302
/healthz/live 200 200
/healthz/ready 200 200
/admin/history 200 302
/admin/members 200 302
/admin/analytics 200 302
/characters 302 302
/account/profile 302 302
/roster/999999 404 404
/no-such-page 404 404
/admin/path 302 302
"

# The seeded roster page, which only exists locally.
if [ -n "$ROSTER_ID" ]; then
    ROUTES="$ROUTES
/roster/$ROSTER_ID 200 200"
fi

fails=0
while read -r path want_local want_remote; do
    [ -z "$path" ] && continue
    want=$([ "$LOCAL" = 1 ] && echo "$want_local" || echo "$want_remote")
    got=$(curl -s -o /dev/null -w '%{http_code}' --max-time 25 "$BASE$path" || echo "000")
    if [ "$got" = "$want" ]; then
        printf '  ok   %-26s %s\n' "$path" "$got"
    else
        printf '  FAIL %-26s got %s want %s\n' "$path" "$got" "$want"
        fails=$((fails + 1))
    fi
done <<< "$ROUTES"

if [ -n "$ROSTER_ID" ]; then
    body=$(curl -s --max-time 25 "$BASE/roster/$ROSTER_ID" || true)
    for want in "Smoke" "Smoketest" "Champion"; do
        if echo "$body" | grep -q "$want"; then
            printf '  ok   %-26s renders %s\n' "/roster/$ROSTER_ID" "$want"
        else
            printf '  FAIL %-26s missing %s\n' "/roster/$ROSTER_ID" "$want"
            fails=$((fails + 1))
        fi
    done
    # The alias, not the Discord name. This is the bug that started the audit.
    if echo "$body" | grep -q "smoketest_x9"; then
        echo "  FAIL /roster/$ROSTER_ID leaks the Discord name instead of the alias"
        fails=$((fails + 1))
    else
        printf '  ok   %-26s names by alias, not Discord\n' "/roster/$ROSTER_ID"
    fi
fi

# The calculator is approved members only, so an anonymous caller must see none of
# it. Checking the body for absence is worth more than the status code alone: a
# page that 302s but still renders its content in the response body would pass a
# status check and leak everything.
body=$(curl -s --max-time 25 "$BASE/tools/daoc/spellcraft" || true)
for leak in "Item slot" "Pick a slot" "Save template" "Sockets"; do
    if echo "$body" | grep -q "$leak"; then
        printf '  FAIL %-26s leaks %s to an anonymous caller\n' "/tools/daoc/spellcraft" "$leak"
        fails=$((fails + 1))
    else
        printf '  ok   %-26s no %s when signed out\n' "/tools/daoc/spellcraft" "$leak"
    fi
done

# The shelf still has to say why Open will bounce them.
shelf=$(curl -s --max-time 25 "$BASE/tools/daoc" || true)
if echo "$shelf" | grep -qi "Approved members only"; then
    printf '  ok   %-26s says the calculator is members only\n' "/tools/daoc"
else
    echo "  FAIL /tools/daoc does not say the calculator is members only"
    fails=$((fails + 1))
fi

if [ "$LOCAL" = 1 ]; then
    # A 200 that logged a swallowed exception is still a broken page.
    n=$(docker compose logs web 2>&1 | grep -c 'An unhandled exception' || true)
    if [ "$n" != "0" ]; then
        echo "  FAIL $n unhandled exception(s) in the web log"
        fails=$((fails + 1))
    else
        echo "  ok   no unhandled exceptions in the log"
    fi
fi

if [ "$fails" != "0" ]; then
    echo "$fails failure(s) against $BASE"
    exit 1
fi
echo "all routes ok against $BASE"

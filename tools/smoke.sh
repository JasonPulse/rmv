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
    docker image rm -f rmv-web:local >/dev/null 2>&1 || true
}

# On the way out however we leave, including a failed route, so a broken run does
# not leave containers and a database volume behind.
trap teardown EXIT

if [ "$BASE" = "http://localhost:5080" ]; then
    LOCAL=1
    ASPNETCORE_ENVIRONMENT=Development docker compose up -d --build >/dev/null
    printf 'waiting for %s ' "$BASE"
    for _ in $(seq 1 40); do
        if [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$BASE/healthz/ready" || true)" = "200" ]; then
            break
        fi
        printf '.'; sleep 2
    done
    echo
fi

# path expected_local expected_remote
ROUTES="
/ 200 200
/history 200 200
/tools 200 200
/tools/daoc/roll-parser 200 200
/healthz/live 200 200
/healthz/ready 200 200
/roster/1 200 200
/admin/history 200 302
/admin/members 200 302
/admin/analytics 200 302
/characters 302 302
/account/profile 302 302
/no-such-page 404 404
"

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

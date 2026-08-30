using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace Rmv.Web.Data;

/// <summary>
/// A Discord account, as either half of the site sees it.
///
/// The sign-in hook has a JSON payload and no principal yet; every later request
/// has a principal and no payload. Both describe the same three facts, so both
/// arrive here as this and there is one way to write a member row.
/// </summary>
public sealed record DiscordIdentity(string Id, string DisplayName, string? AvatarHash);

/// <summary>
/// The only thing that writes a member row.
///
/// Two things used to. The sign-in hook in Program.cs upserted the row from the
/// OAuth payload, and this class created it on access, and the two did not agree:
/// the hook set IsAdmin false and left Status at its default, so a root admin's
/// first ever row said Pending while configuration said they ran the site. That is
/// the row the admin table printed "PENDING" next to "ROOT", and it is the same
/// mistake as the access bug one layer down. Reconciling it afterwards, which is
/// what the previous fix did, only papered over having two writers.
///
/// Now there is one, and the two callers differ by a single flag: a sign-in also
/// refreshes the name and avatar Discord just handed us, and an ordinary request
/// does not, because a write per page view is not worth a display name being an
/// hour stale.
///
/// Creating on access matters separately. A session outlives a deployment now that
/// the Data Protection key ring is in Postgres, so someone can hold a valid cookie
/// from before the sign-in hook existed, or from a sign-in where it failed. Telling
/// them to sign out and back in is a poor answer, especially when signing out was
/// itself broken once.
/// </summary>
public sealed class MemberDirectory(
    RmvDbContext db, IConfiguration config, ILogger<MemberDirectory> log)
{
    /// <summary>Recorded as the approver, so the audit trail says why.</summary>
    private const string RootSource = "Admin:DiscordIds";

    /// <summary>
    /// The signed-in member, creating the row if it is missing.
    ///
    /// Does not touch the name or avatar: the claims it would copy from came off
    /// this same row at sign-in.
    /// </summary>
    public Task<Member?> EnsureAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        return IdentityOf(user) is { } who
            ? UpsertAsync(who, refreshProfile: false, ct)
            : Task.FromResult<Member?>(null);
    }

    /// <summary>
    /// Records a sign-in: the row, plus the name and avatar Discord just gave us.
    ///
    /// Called from the OAuth hook, which is why it takes the identity rather than a
    /// principal. There is no principal at that point in the handshake.
    /// </summary>
    public Task<Member?> RecordSignInAsync(DiscordIdentity who, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(who);

        return UpsertAsync(who, refreshProfile: true, ct);
    }

    /// <summary>
    /// The claims half of a <see cref="DiscordIdentity"/>, or null when the
    /// principal carries no Discord id.
    /// </summary>
    private static DiscordIdentity? IdentityOf(ClaimsPrincipal user) =>
        DiscordUser.Id(user) is { Length: > 0 } id
            ? new DiscordIdentity(id, DiscordUser.Name(user), user.FindFirst(DiscordUser.AvatarClaim)?.Value)
            : null;

    private async Task<Member?> UpsertAsync(
        DiscordIdentity who, bool refreshProfile, CancellationToken ct)
    {
        var existing = await db.Members.FirstOrDefaultAsync(m => m.DiscordId == who.Id, ct);
        if (existing is not null)
        {
            return await UpdateAsync(existing, who, refreshProfile, ct);
        }

        var now = DateTimeOffset.UtcNow;
        var root = AdminPolicy.IsRootAdmin(config, who.Id);

        var member = new Member
        {
            DiscordId = who.Id,
            DisplayName = who.DisplayName,
            AvatarHash = who.AvatarHash,
            // Pending for any ordinary new sign-in: that is the whole approval gate.
            // A root admin is the exception, because configuration already says who
            // they are and a row that disagreed would only mislead the pages that
            // read it.
            Status = root ? MemberStatus.Approved : MemberStatus.Pending,
            IsAdmin = root,
            ApprovedAt = root ? now : null,
            ApprovedBy = root ? RootSource : null,
            FirstSeenAt = now,
            LastSeenAt = now,
        };

        db.Members.Add(member);

        try
        {
            await db.SaveChangesAsync(ct);
            return member;
        }
        catch (DbUpdateException ex)
        {
            // Two requests raced, or a sign-in raced the first page load. The unique
            // index on DiscordId settled it, so read back whichever won rather than
            // failing the page.
            log.LogInformation(ex, "Member {Id} was created concurrently.", who.Id);
            db.Entry(member).State = EntityState.Detached;

            var won = await db.Members.FirstOrDefaultAsync(m => m.DiscordId == who.Id, ct);
            return won is null ? null : await UpdateAsync(won, who, refreshProfile, ct);
        }
    }

    /// <summary>
    /// Brings an existing row up to date: what Discord says, when asked, and what
    /// configuration says, always.
    ///
    /// Writes only when something actually changed, so an ordinary page view is a
    /// read.
    /// </summary>
    private async Task<Member> UpdateAsync(
        Member member, DiscordIdentity who, bool refreshProfile, CancellationToken ct)
    {
        var changed = false;

        if (refreshProfile)
        {
            member.DisplayName = who.DisplayName;
            member.AvatarHash = who.AvatarHash;
            member.LastSeenAt = DateTimeOffset.UtcNow;
            changed = true;
        }

        changed |= MatchConfig(member);

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }

        return member;
    }

    /// <summary>
    /// Brings a root admin's row into line with the configuration that already
    /// grants them everything. Returns whether anything needed changing.
    ///
    /// Nothing derives access from this row any more, so this is not what makes
    /// root work; see Access. It is what stops the row lying to the admin table,
    /// which is where "PENDING" appeared next to "ROOT".
    ///
    /// Blocking a root admin is not something this application can do: the grant is
    /// in configuration, checked without the database, precisely so a bad grant or
    /// an outage cannot lock you out of your own site. A row saying Blocked would
    /// have been a lie rather than a restriction, so it is corrected too.
    /// </summary>
    private bool MatchConfig(Member member)
    {
        if (!AdminPolicy.IsRootAdmin(config, member.DiscordId))
        {
            return false;
        }

        if (member is { Status: MemberStatus.Approved, IsAdmin: true })
        {
            return false;
        }

        log.LogInformation(
            "Root admin {Id} was {Status} with IsAdmin {IsAdmin}; matching configuration.",
            member.DiscordId, member.Status, member.IsAdmin);

        member.Status = MemberStatus.Approved;
        member.IsAdmin = true;
        member.ApprovedAt ??= DateTimeOffset.UtcNow;
        member.ApprovedBy ??= RootSource;

        return true;
    }
}

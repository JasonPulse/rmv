using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace Rmv.Web.Data;

/// <summary>
/// Finds the signed-in member, creating the row if it is missing.
///
/// The sign-in hook records members, but a session outlives a deployment now that
/// the Data Protection key ring is in Postgres. So someone can hold a perfectly
/// valid cookie from before that hook existed, or from a sign-in where it failed,
/// and have no row. Telling them to sign out and back in is a poor answer,
/// especially when signing out was itself broken.
///
/// Creating on access makes the row a consequence of being signed in rather than
/// of having signed in at the right moment.
///
/// It also makes a root admin's row true. Their access comes from configuration and
/// the policies check that before they touch the database, so an earlier version
/// left the row saying Pending and reasoned that this granted nothing. That was
/// only true of the policies. Half the site asks the row instead: the gallery
/// decides whether to offer an upload from Member.CanContribute, and whether to
/// offer removing someone else's screenshot from Member.CanAdminister. A root admin
/// with a Pending row was shown neither while passing every policy, and the admin
/// table read "PENDING" next to "ROOT".
/// </summary>
public sealed class MemberDirectory(
    RmvDbContext db, IConfiguration config, ILogger<MemberDirectory> log)
{
    public async Task<Member?> EnsureAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var discordId = DiscordUser.Id(user);
        if (string.IsNullOrEmpty(discordId))
        {
            return null;
        }

        var existing = await db.Members.FirstOrDefaultAsync(m => m.DiscordId == discordId, ct);
        if (existing is not null)
        {
            // Every access, not only creation, so a row that predates this fix is
            // corrected the next time its owner loads a page.
            return await MatchConfigAsync(existing, ct);
        }

        var now = DateTimeOffset.UtcNow;
        var root = AdminPolicy.IsRootAdmin(config, discordId);

        var member = new Member
        {
            DiscordId = discordId,
            DisplayName = DiscordUser.Name(user),
            AvatarHash = user.FindFirst(DiscordUser.AvatarClaim)?.Value,
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
            // Two requests raced. The unique index on DiscordId settled it, so
            // read back whichever won rather than failing the page.
            log.LogInformation(ex, "Member {Id} was created concurrently.", discordId);
            db.Entry(member).State = EntityState.Detached;

            var won = await db.Members.FirstOrDefaultAsync(m => m.DiscordId == discordId, ct);
            return won is null ? null : await MatchConfigAsync(won, ct);
        }
    }

    /// <summary>Recorded as the approver, so the audit trail says why.</summary>
    private const string RootSource = "Admin:DiscordIds";

    /// <summary>
    /// Brings a root admin's row into line with the configuration that already
    /// grants them everything.
    ///
    /// Blocking a root admin is not something this application can do: the policies
    /// check the configured ids before they read the database, precisely so a bad
    /// grant or an outage cannot lock you out of your own site. A row saying Blocked
    /// would have been a lie rather than a restriction, so it is corrected too.
    ///
    /// Writes only when something is actually wrong, so this is a read for everyone
    /// on every other request.
    /// </summary>
    private async Task<Member> MatchConfigAsync(Member member, CancellationToken ct)
    {
        if (!AdminPolicy.IsRootAdmin(config, member.DiscordId))
        {
            return member;
        }

        if (member is { Status: MemberStatus.Approved, IsAdmin: true })
        {
            return member;
        }

        log.LogInformation(
            "Root admin {Id} was {Status} with IsAdmin {IsAdmin}; matching configuration.",
            member.DiscordId, member.Status, member.IsAdmin);

        member.Status = MemberStatus.Approved;
        member.IsAdmin = true;
        member.ApprovedAt ??= DateTimeOffset.UtcNow;
        member.ApprovedBy ??= RootSource;

        await db.SaveChangesAsync(ct);

        return member;
    }
}

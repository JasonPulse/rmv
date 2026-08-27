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
/// </summary>
public sealed class MemberDirectory(RmvDbContext db, ILogger<MemberDirectory> log)
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
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var member = new Member
        {
            DiscordId = discordId,
            DisplayName = DiscordUser.Name(user),
            AvatarHash = user.FindFirst(DiscordUser.AvatarClaim)?.Value,
            // Pending, like any new sign-in. A root admin's access comes from
            // configuration, so backfilling a row does not grant anything.
            Status = MemberStatus.Pending,
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
            return await db.Members.FirstOrDefaultAsync(m => m.DiscordId == discordId, ct);
        }
    }
}

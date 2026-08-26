using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Rmv.Web.Data;

public sealed class AdminRequirement : IAuthorizationRequirement;

/// <summary>
/// Authorisation, as distinct from authentication.
///
/// Discord sign-in proves someone has a Discord account, which is not a
/// qualification for editing the site. Admin comes from one of two places:
///
///   1. Admin:DiscordIds in configuration. These are root admins. They cannot be
///      revoked from inside the app, and they still work when the database is
///      unreachable, which is what stops a bad grant or an outage locking you
///      out of your own site.
///   2. Member.IsAdmin in the database, granted by an existing admin at
///      /admin/members.
///
/// It fails closed: no config ids and no database admin means nobody is an
/// admin, rather than everybody.
/// </summary>
public static class AdminPolicy
{
    public const string Name = "Admin";

    /// <summary>Comma or whitespace separated Discord user ids, from Admin:DiscordIds.</summary>
    public static string[] Parse(string? configured) =>
        (configured ?? "")
            .Split([',', ' ', ';', '\n', '\r', '\t'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public static bool IsRootAdmin(IConfiguration config, string? discordId) =>
        discordId is not null
        && Parse(config["Admin:DiscordIds"]).Contains(discordId, StringComparer.Ordinal);
}

/// <summary>
/// Scoped, so it can resolve the scoped DbContext. Registered even when no
/// database exists, in which case only config admins pass.
/// </summary>
public sealed class AdminAuthorizationHandler(
    IServiceProvider services,
    IConfiguration config,
    ILogger<AdminAuthorizationHandler> log) : AuthorizationHandler<AdminRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        var id = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        // Checked first and without touching the database, so a root admin can
        // always get in to fix things.
        if (AdminPolicy.IsRootAdmin(config, id))
        {
            context.Succeed(requirement);
            return;
        }

        var db = services.GetService<RmvDbContext>();
        if (db is null)
        {
            return;
        }

        try
        {
            if (await db.Members.AnyAsync(m => m.DiscordId == id && m.IsAdmin))
            {
                context.Succeed(requirement);
            }
        }
        catch (Exception ex)
        {
            // Database down. Root admins already succeeded above; everyone else
            // is denied rather than allowed.
            log.LogWarning(ex, "Could not check admin status for {Id}.", id);
        }
    }
}

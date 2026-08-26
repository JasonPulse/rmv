using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Rmv.Web.Data;

/// <summary>
/// Authorisation, as distinct from authentication.
///
/// Discord sign-in proves someone has a Discord account, which is not a
/// qualification for editing the site. Admin pages therefore require the
/// caller's Discord user id to appear in a configured allowlist, not merely a
/// valid cookie. Without this, wiring Discord would let anyone on Discord edit
/// the guild history.
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

    public static void Configure(AuthorizationOptions options, string[] adminIds)
    {
        options.AddPolicy(Name, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx =>
            {
                // No admins configured means no admin access, rather than open
                // access. Failing closed is the only safe default here.
                if (adminIds.Length == 0)
                {
                    return false;
                }

                var id = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                return id is not null && adminIds.Contains(id, StringComparer.Ordinal);
            }));
    }
}

using System.Globalization;
using System.Security.Claims;

namespace Rmv.Web.Data;

/// <summary>
/// Reads the signed-in Discord identity off the claims principal.
///
/// The avatar hash is added in Program.cs from the OAuth payload rather than via
/// a claim-mapping helper, so the null handling is explicit and there is no
/// guesswork about which extension namespace to import.
/// </summary>
public static class DiscordUser
{
    public const string AvatarClaim = "urn:discord:avatar";

    public static string? Id(ClaimsPrincipal user) =>
        user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public static string Name(ClaimsPrincipal user) =>
        user.Identity?.Name is { Length: > 0 } n ? n : "Member";

    /// <summary>
    /// The CDN URL for this user's avatar, or null when they have none set.
    ///
    /// Note this is a request to Discord's CDN, the only third-party request the
    /// site makes. It happens on admin pages for signed-in members only, who are
    /// already using Discord. Returning null falls back to initials, which keeps
    /// the page working if that ever needs to change.
    /// </summary>
    public static string? AvatarUrl(ClaimsPrincipal user, int size = 64)
    {
        var id = Id(user);
        var hash = user.FindFirst(AvatarClaim)?.Value;

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }

        // Both come from Discord and are checked before use: the id is digits and
        // the hash is hex, so neither can break out of the URL.
        if (!id.All(char.IsAsciiDigit) || !hash.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            return null;
        }

        var ext = hash.StartsWith("a_", StringComparison.Ordinal) ? "gif" : "png";
        return string.Create(CultureInfo.InvariantCulture,
            $"https://cdn.discordapp.com/avatars/{id}/{hash}.{ext}?size={size}");
    }
}

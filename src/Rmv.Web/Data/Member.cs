namespace Rmv.Web.Data;

/// <summary>
/// Someone who has signed in with Discord at least once. Created on first
/// sign-in, so the admin screen has real people to promote rather than asking
/// for ids to be typed in.
/// </summary>
public class Member
{
    public int Id { get; set; }

    /// <summary>Discord snowflake. The stable identity; the display name changes.</summary>
    public string DiscordId { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string? AvatarHash { get; set; }

    /// <summary>
    /// Granted through /admin/members. Separate from the config allowlist, which
    /// is the bootstrap and cannot be revoked from inside the app.
    /// </summary>
    public bool IsAdmin { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}

namespace Rmv.Web.Data;

/// <summary>
/// Where a member stands. New sign-ins land on Pending, so signing in with
/// Discord gets you a seat and nothing else: it does not let a stranger start
/// adding characters.
/// </summary>
public enum MemberStatus
{
    /// <summary>Signed in, waiting on an admin. Can see the site, can change nothing.</summary>
    Pending,

    /// <summary>Approved by an admin. Can add and claim characters.</summary>
    Approved,

    /// <summary>Refused or removed. Kept rather than deleted so they cannot re-register by signing in again.</summary>
    Blocked,
}

/// <summary>
/// Someone who has signed in with Discord at least once. Created on first
/// sign-in, so the admin screen has real people to approve rather than asking
/// for ids to be typed in.
/// </summary>
public class Member
{
    public int Id { get; set; }

    /// <summary>Discord snowflake. The stable identity; the display name changes.</summary>
    public string DiscordId { get; set; } = "";

    /// <summary>Whatever Discord calls them. Updated on every sign-in.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// What they want to be called here.
    ///
    /// Discord names are whoever got there first, so they rarely match the name
    /// people know each other by in game. The alias is member-editable and is
    /// what the site shows; the Discord name is only a fallback.
    /// </summary>
    public string? Alias { get; set; }

    public string? AvatarHash { get; set; }

    /// <summary>
    /// Pending until an admin says otherwise. Defaulting to Approved would make
    /// the approval step decorative.
    /// </summary>
    public MemberStatus Status { get; set; } = MemberStatus.Pending;

    /// <summary>
    /// Granted through /admin/members. Separate from the config allowlist, which
    /// is the bootstrap and cannot be revoked from inside the app.
    /// </summary>
    public bool IsAdmin { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Null while pending.</summary>
    public DateTimeOffset? ApprovedAt { get; set; }

    /// <summary>Display name of the admin who approved them, for the audit trail.</summary>
    public string? ApprovedBy { get; set; }

    public bool CanContribute => Status == MemberStatus.Approved;

    /// <summary>The name to show. Never the Discord id.</summary>
    public string Handle => string.IsNullOrWhiteSpace(Alias) ? DisplayName : Alias;
}

namespace Rmv.Web.Data;

/// <summary>
/// What one person may do here. One answer, computed in one place.
///
/// Access has two sources and always will: the root ids in Admin:DiscordIds, and
/// the member row. Both exist on purpose. Configuration is checked without the
/// database so a bad grant or an outage cannot lock you out of your own site, and
/// the row is what admins actually edit.
///
/// Having two sources is fine. Having two folds is not, and that was the bug: the
/// authorization policies folded configuration then row, while the gallery and the
/// profile page read the row alone. A root admin whose row still said Pending
/// passed every policy and was shown no upload button, and the admin table printed
/// "PENDING" next to "ROOT". Nothing textual is shared between those two readings,
/// so no amount of looking for duplicated code finds it.
///
/// So there is now one fold, <see cref="Of"/>, and one thing that calls it,
/// <see cref="CurrentMember.AccessAsync"/>. The authorization handlers are adapters
/// over that answer rather than a second implementation of it, and no page decides
/// anything for itself. Reconciling two answers was the previous fix, and it was
/// the wrong one; there is only one answer to reconcile now.
/// </summary>
/// <param name="DiscordId">Null for an anonymous caller.</param>
/// <param name="Member">
/// Their row, when there is one. Null for an anonymous caller, when the site is
/// running without a database, and when the database could not be read.
/// </param>
/// <param name="IsRoot">Named in Admin:DiscordIds. Cannot be revoked from inside the app.</param>
public sealed record Access(
    string? DiscordId,
    Member? Member,
    bool IsRoot,
    bool CanContribute,
    bool CanAdminister)
{
    /// <summary>Nobody. What an anonymous caller gets, and what every failure falls back to.</summary>
    public static readonly Access None = new(null, null, false, false, false);

    public bool SignedIn => !string.IsNullOrEmpty(DiscordId);

    /// <summary>
    /// Signed in and still waiting on an admin. The state the site has to say out
    /// loud, because otherwise signing in looks like it did nothing.
    /// </summary>
    public bool Pending => SignedIn && !CanContribute && Member?.Status != MemberStatus.Blocked;

    public bool Blocked => Member?.Status == MemberStatus.Blocked && !IsRoot;

    /// <summary>The name to show. Never the Discord id.</summary>
    public string? Handle => Member?.Handle;

    /// <summary>
    /// The one fold, from the two sources to the two answers.
    ///
    /// Root first, and without needing a row at all: that is what makes a root
    /// admin work during an outage, and it is why this takes a member that may be
    /// null rather than requiring one.
    ///
    /// Blocked beats admin, deliberately, so revoking someone does not depend on
    /// remembering to clear the admin flag too. It does not beat root, because the
    /// application cannot block a root id: the grant is in configuration, and a row
    /// saying otherwise would be a lie rather than a restriction.
    ///
    /// Administering implies contributing. An admin who can edit the site but not
    /// add a character is nonsense, and /admin/members approves on promotion anyway.
    /// </summary>
    public static Access Of(string? discordId, Member? member, bool isRoot)
    {
        if (string.IsNullOrEmpty(discordId))
        {
            return None;
        }

        var administers = isRoot || member is { IsAdmin: true, Status: not MemberStatus.Blocked };
        var contributes = administers || member?.Status == MemberStatus.Approved;

        return new Access(discordId, member, isRoot, contributes, administers);
    }
}

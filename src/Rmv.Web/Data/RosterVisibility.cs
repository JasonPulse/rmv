namespace Rmv.Web.Data;

/// <summary>
/// Who appears publicly, in one place.
///
/// A blocked member is off the roster, and everything of theirs goes with them:
/// their characters vanish from the history page and the leaderboards, their
/// screenshots leave the gallery, their portraits stop being served, and their
/// roster page 404s. That is one rule with seven consequences, and it was written
/// out seven times, twice inverted. They agreed, so nothing was broken, but the
/// next status added to the enum would have been applied in some of the seven and
/// missed in the rest, which is exactly how the root-admin bug happened.
///
/// So the rule is the list below and nothing else. Add a status and every listing,
/// endpoint and page follows without being visited.
/// </summary>
public static class RosterVisibility
{
    /// <summary>
    /// The statuses that appear publicly.
    ///
    /// Pending is in it deliberately. Someone who has signed in and is waiting on
    /// an admin cannot add anything, so they own nothing to hide; leaving them out
    /// would only mean a character that disappears from the history page for as
    /// long as an approval takes.
    /// </summary>
    public static readonly MemberStatus[] Visible =
    [
        MemberStatus.Pending,
        MemberStatus.Approved,
    ];

    /// <summary>
    /// Whether this member and their content appear publicly. Null is no: an
    /// orphaned row belongs to nobody, so it is nobody's to show.
    /// </summary>
    public static bool Shows(Member? member) =>
        member is not null && Visible.Contains(member.Status);

    /// <summary>Characters whose owner appears publicly. Translates to an IN clause.</summary>
    public static IQueryable<Character> OnRoster(this IQueryable<Character> characters) =>
        characters.Where(c => c.Member != null && Visible.Contains(c.Member.Status));

    /// <summary>Screenshots whose owner appears publicly.</summary>
    public static IQueryable<Screenshot> OnRoster(this IQueryable<Screenshot> shots) =>
        shots.Where(s => s.Member != null && Visible.Contains(s.Member.Status));

    /// <summary>
    /// Characters a herald can answer for: fetched, on a game with an adapter
    /// chosen.
    ///
    /// Whether that adapter still exists is a question for the registry, which
    /// cannot be asked in SQL, so callers check it after this.
    /// </summary>
    public static IQueryable<Character> FromHerald(this IQueryable<Character> characters) =>
        characters.Where(c => c.Source == CharacterSource.Herald
                              && c.Game != null
                              && c.Game.HeraldAdapterKey != null);
}

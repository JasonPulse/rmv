using Rmv.Web.Data;

namespace Rmv.Web.Herald;

/// <summary>One place on a board.</summary>
/// <param name="Position">1-based, and shared by a tie.</param>
public sealed record LeaderboardRow(int Position, Character Character, long Value);

/// <summary>
/// Turns a game's characters into an ordered board.
///
/// Pure, and separate from the page, so the tie handling and the exclusions can be
/// tested without a database. The page's job is to fetch rows and render them.
/// </summary>
public static class Leaderboard
{
    /// <summary>
    /// Highest first, ties sharing a position, and anything measuring zero left
    /// out.
    ///
    /// Zero is not a low score, it is an absent one: a character the herald has not
    /// answered for yet, or one on a herald that does not publish the measure this
    /// board ranks on. Listing them at the bottom would read as a result.
    /// </summary>
    public static IReadOnlyList<LeaderboardRow> Rank(
        IEnumerable<Character> characters, RankBy by)
    {
        var ranked = characters
            .Select(c => (Character: c, Value: Value(c, by)))
            .Where(x => x.Value > 0)
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Character.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<LeaderboardRow>(ranked.Count);
        var position = 0;
        long? previous = null;

        for (var i = 0; i < ranked.Count; i++)
        {
            // A tie shares a position and the next value takes the place it would
            // have had, so two firsts are followed by a third rather than a second.
            if (ranked[i].Value != previous)
            {
                position = i + 1;
                previous = ranked[i].Value;
            }

            rows.Add(new LeaderboardRow(position, ranked[i].Character, ranked[i].Value));
        }

        return rows;
    }

    public static long Value(Character c, RankBy by) => by switch
    {
        RankBy.Score => c.Score ?? 0,
        RankBy.Level => c.Level ?? 0,
        _ => 0,
    };
}

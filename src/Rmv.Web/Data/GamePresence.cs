namespace Rmv.Web.Data;

/// <summary>
/// One game the guild has played, and under which tags. "Where were we, where
/// are we now."
/// </summary>
public class GamePresence
{
    public int Id { get; set; }

    /// <summary>e.g. "Blackthorn DAoC", "Final Fantasy XI".</summary>
    public string Game { get; set; } = "";

    /// <summary>
    /// Free text, as it reads on the page: "RMV, Legends, Dark Auspices". Kept as
    /// one field rather than a child table because that is how it is written and
    /// how it is edited; splitting on commas for display is a view concern.
    /// </summary>
    public string Guilds { get; set; } = "";

    /// <summary>Optional, e.g. "2001-2012".</summary>
    public string? Period { get; set; }

    public bool IsActive { get; set; }

    /// <summary>Ascending within each of the active and inactive lists.</summary>
    public int SortOrder { get; set; }

    public List<GameLink> Links { get; set; } = [];

    public List<Character> Characters { get; set; } = [];

    /// <summary>
    /// Which herald adapter handles this game, e.g. "blackthorn". Null means
    /// characters cannot be added: there is nowhere to look them up.
    /// </summary>
    public string? HeraldAdapterKey { get; set; }

    /// <summary>
    /// Optional override for the adapter's own address, for the day a server
    /// changes domain. Normally null: the adapter knows where its herald lives.
    /// </summary>
    public string? HeraldBaseUrl { get; set; }

    /// <summary>
    /// Whether an adapter has been chosen. Whether that adapter actually exists
    /// is a question for the registry, not for the row.
    /// </summary>
    public bool HasHerald => !string.IsNullOrWhiteSpace(HeraldAdapterKey);

    public IEnumerable<string> GuildList() => Guilds
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The year this presence ended, for putting the newest first.
    ///
    /// Read out of Period rather than stored, because Period is already the thing
    /// an admin types and every value in it is a year range: "2001-2005",
    /// "2025-Present". Adding a date field would mean re-entering twenty years of
    /// history to sort a page.
    ///
    /// Ongoing beats every year, which is what "Present" means. An unparseable or
    /// missing period sorts last rather than first, so a typo does not silently
    /// jump a game to the top of the page.
    /// </summary>
    public int EndYear => YearRange().End;

    /// <summary>The year it started, to break a tie between two that ended together.</summary>
    public int StartYear => YearRange().Start;

    /// <summary>
    /// Newest first. Active games lead regardless of what their period says, so a
    /// current game with no period filled in is still at the top where it belongs.
    /// </summary>
    public (bool NotActive, int EndDesc, int StartDesc, int SortOrder, string Game) NewestFirst =>
        (!IsActive, -EndYear, -StartYear, SortOrder, Game);

    private (int Start, int End) YearRange()
    {
        if (string.IsNullOrWhiteSpace(Period))
        {
            return (0, 0);
        }

        var years = new List<int>();

        // Every four-digit run in the text. Simpler than a date parse and it does
        // not care whether the separator is a hyphen, an en dash or the word "to".
        for (var i = 0; i <= Period.Length - 4; i++)
        {
            if (Period.AsSpan(i, 4).ContainsAnyExcept("0123456789"))
            {
                continue;
            }

            // Not part of a longer number.
            var before = i == 0 || !char.IsAsciiDigit(Period[i - 1]);
            var after = i + 4 >= Period.Length || !char.IsAsciiDigit(Period[i + 4]);

            if (before && after && int.TryParse(Period.AsSpan(i, 4), out var year))
            {
                years.Add(year);
                i += 3;
            }
        }

        // Ongoing, however it is worded. Higher than any year, so it leads.
        var ongoing = Period.Contains("present", StringComparison.OrdinalIgnoreCase)
                      || Period.Contains("now", StringComparison.OrdinalIgnoreCase)
                      || Period.TrimEnd().EndsWith('-');

        var start = years.Count > 0 ? years[0] : 0;
        var end = ongoing ? int.MaxValue : years.Count > 0 ? years[^1] : 0;

        return (start, end);
    }
}

/// <summary>
/// The order games are listed in.
///
/// Two orders, and both live here. The history page and the leaderboards read
/// twenty years newest first, out of Period; everything that offers games as a
/// list, including both add forms and the admin editor, reads them in the order an
/// admin arranged them.
///
/// This was four orderings across five files, and two of them ignored SortOrder
/// entirely, so arranging the admin list did nothing to the dropdown a member picks
/// a game from.
/// </summary>
public static class GameOrder
{
    /// <summary>Active first, then as an admin arranged them, then by name.</summary>
    public static IOrderedQueryable<GamePresence> Listed(this IQueryable<GamePresence> games) =>
        games.OrderByDescending(g => g.IsActive)
            .ThenBy(g => g.SortOrder)
            .ThenBy(g => g.Game);
}

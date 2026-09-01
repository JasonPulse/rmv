using System.Globalization;
using AngleSharp.Dom;

namespace Rmv.Web.Herald;

/// <summary>
/// The Blackthorn character page, turned into a HeraldCharacter.
///
/// Separate from the adapter so the tests can drive exactly the code that ships,
/// against real saved markup, with no network involved.
/// </summary>
public static class BlackthornParser
{
    public static HeraldCharacter Parse(string characterName, IDocument doc, string url)
    {
        var labels = ReadLabelledCells(doc);

        return new HeraldCharacter
        {
            // Taken from what was asked for, not from the page: the name cell
            // also carries badges, e.g. "Enchantress AutoHotKey".
            Name = characterName,
            Guild = Clean(labels.GetValueOrDefault("guild")),
            Realm = Clean(labels.GetValueOrDefault("realm")),
            Class = Clean(labels.GetValueOrDefault("class")),
            Race = Clean(labels.GetValueOrDefault("race")),
            Level = ParseInt(labels.GetValueOrDefault("lvl")),
            RealmRank = Clean(labels.GetValueOrDefault("rr")),
            LastOnline = Clean(labels.GetValueOrDefault("last online")),
            RealmPoints = ReadAllTimeStat(doc, "RealmPoints"),
            Kills = ReadAllTimeStat(doc, "Kills"),
            Deaths = ReadAllTimeStat(doc, "Deaths"),
            Url = url,
            Stats = ReadStats(doc, labels),
        };
    }

    /// <summary>
    /// What this herald publishes that no other one does.
    ///
    /// The page is a matrix: fourteen stats down, five periods across, so seventy
    /// numbers of which three fit the shared fields. Realm points for last week was a
    /// token in the 2001 generator, %W, and was the obvious thing missing until this.
    ///
    /// Not all seventy. A palette of seventy is unusable and a signature has room for
    /// four lines; these are the ones somebody would actually put in one.
    /// </summary>
    private static Dictionary<string, string> ReadStats(IDocument doc, Dictionary<string, string> labels)
    {
        var stats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string key, long? value)
        {
            if (value is { } n)
            {
                stats[key] = n.ToString("N0", CultureInfo.InvariantCulture);
            }
        }

        // The periods that mean something in a signature. "This month" and "last
        // month" are on the page too and nobody has ever put them in one.
        Add("ThisWeek", ReadStat(doc, "RealmPoints", "This Week"));
        Add("LastWeek", ReadStat(doc, "RealmPoints", "Last Week"));

        Add("Solo", ReadStat(doc, "Solo", "All Time"));
        Add("SoloWeek", ReadStat(doc, "Solo", "This Week"));
        Add("DeathBlows", ReadStat(doc, "DB", "All Time"));
        Add("Keeps", ReadStat(doc, "Keeps", "All Time"));
        Add("Relics", ReadStat(doc, "Relics", "All Time"));
        Add("AlbionKills", ReadStat(doc, "Albion Kills", "All Time"));
        Add("MidgardKills", ReadStat(doc, "Midgard Kills", "All Time"));
        Add("HiberniaKills", ReadStat(doc, "Hibernia Kills", "All Time"));

        // A ratio keeps its decimal, so it is read as text rather than a count.
        if (ReadStatText(doc, "K/D Ratio", "All Time") is { Length: > 0 } ratio)
        {
            stats["Ratio"] = ratio;
        }

        // The fair group fights block, which is its own little table.
        foreach (var (label, key) in new[]
                 {
                     ("Total Fights", "Fights"),
                     ("Wins", "Wins"),
                     ("Losses", "Losses"),
                     ("Win Rate", "WinRate"),
                 })
        {
            if (labels.GetValueOrDefault(label) is { Length: > 0 } value && value != "-")
            {
                stats[key] = value;
            }
        }

        return stats;
    }

    /// <summary>
    /// Every table worth reading, as its header cells and its rows.
    ///
    /// Both readers below walked the tables themselves and dropped anything with
    /// fewer than two rows, in the same five lines. The page has eight tables and
    /// the interesting values sit in different ones, so the walk is the part they
    /// genuinely share.
    /// </summary>
    private static IEnumerable<(string[] Headers, IElement[] Rows)> Tables(IDocument doc)
    {
        foreach (var table in doc.QuerySelectorAll("table"))
        {
            var rows = table.QuerySelectorAll("tr").ToArray();

            // A heading row and nothing under it says nothing.
            if (rows.Length < 2)
            {
                continue;
            }

            yield return (Cells(rows[0]), rows);
        }
    }

    /// <summary>th and td alike: the page uses both for headings.</summary>
    private static string[] Cells(IElement row) =>
        row.QuerySelectorAll("th, td").Select(Text).ToArray();

    /// <summary>
    /// Walks every table and pairs each heading cell with the cell beneath it.
    /// Handles both shapes the page uses: a one-column table with the label in
    /// the header row, and a multi-column table like "LVL | RR" over "50 | 8L0".
    /// </summary>
    private static Dictionary<string, string> ReadLabelledCells(IDocument doc)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (headers, rows) in Tables(doc))
        {
            var values = Cells(rows[1]);

            for (var i = 0; i < headers.Length && i < values.Length; i++)
            {
                var key = headers[i];
                if (key.Length == 0 || values[i].Length == 0)
                {
                    continue;
                }

                // First writer wins, so a later table cannot clobber a real value.
                found.TryAdd(key, values[i]);
            }
        }

        return found;
    }

    /// <summary>The All Time column, which is what the shared fields want.</summary>
    private static long? ReadAllTimeStat(IDocument doc, string statLabel) =>
        ReadStat(doc, statLabel, "All Time");

    private static long? ReadStat(IDocument doc, string statLabel, string period) =>
        ParseLong(ReadStatText(doc, statLabel, period));

    /// <summary>
    /// One cell of the stats matrix, by its row label and its column heading.
    ///
    /// Both are found by name rather than by position: the page has fourteen rows and
    /// five periods, and counting either would break the day one is added.
    /// </summary>
    private static string? ReadStatText(IDocument doc, string statLabel, string period)
    {
        foreach (var (headers, rows) in Tables(doc))
        {
            var column = Array.FindIndex(headers,
                h => h.Equals(period, StringComparison.OrdinalIgnoreCase));

            if (column < 0)
            {
                continue;
            }

            foreach (var row in rows.Skip(1))
            {
                var cells = Cells(row);
                if (cells.Length <= column)
                {
                    continue;
                }

                if (cells[0].Equals(statLabel, StringComparison.OrdinalIgnoreCase))
                {
                    var value = cells[column];
                    return value is "-" or "" ? null : value;
                }
            }
        }

        return null;
    }

    private static string Text(IElement element) =>
        string.Join(' ', element.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "-" ? null : value.Trim();

    private static int? ParseInt(string? value) =>
        int.TryParse(value?.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : null;

    private static long? ParseLong(string? value) =>
        long.TryParse(value?.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : null;
}

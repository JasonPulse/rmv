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
        };
    }

    /// <summary>
    /// Walks every table and pairs each heading cell with the cell beneath it.
    /// Handles both shapes the page uses: a one-column table with the label in
    /// the header row, and a multi-column table like "LVL | RR" over "50 | 8L0".
    /// </summary>
    private static Dictionary<string, string> ReadLabelledCells(IDocument doc)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in doc.QuerySelectorAll("table"))
        {
            var rows = table.QuerySelectorAll("tr").ToArray();
            if (rows.Length < 2)
            {
                continue;
            }

            var headers = rows[0].QuerySelectorAll("th, td").Select(Text).ToArray();
            var values = rows[1].QuerySelectorAll("th, td").Select(Text).ToArray();

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

    /// <summary>
    /// Reads the All Time column from the stats table. "All Time" is found by its
    /// heading rather than assumed to be last, and the row by its label.
    /// </summary>
    private static long? ReadAllTimeStat(IDocument doc, string statLabel)
    {
        foreach (var table in doc.QuerySelectorAll("table"))
        {
            var rows = table.QuerySelectorAll("tr").ToArray();
            if (rows.Length < 2)
            {
                continue;
            }

            var headers = rows[0].QuerySelectorAll("th, td").Select(Text).ToArray();
            var column = Array.FindIndex(headers,
                h => h.Equals("All Time", StringComparison.OrdinalIgnoreCase));

            if (column < 0)
            {
                continue;
            }

            foreach (var row in rows.Skip(1))
            {
                var cells = row.QuerySelectorAll("th, td").Select(Text).ToArray();
                if (cells.Length <= column)
                {
                    continue;
                }

                if (cells[0].Equals(statLabel, StringComparison.OrdinalIgnoreCase))
                {
                    return ParseLong(cells[column]);
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

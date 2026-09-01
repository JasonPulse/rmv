using System.Text.Json;

namespace Rmv.Web.Herald;

/// <summary>
/// A character's herald-specific stats, on their way to and from the database.
///
/// One place, because the shape is written by CharacterService and read by the
/// signature tokens, and a dictionary serialised two ways is a stat that appears in
/// the editor and never draws.
/// </summary>
public static class HeraldStats
{
    /// <summary>
    /// A cap on what a herald can push into a row. The DAoC page has seventy
    /// numbers; a herald that decides to publish a thousand does not get to.
    /// </summary>
    public const int MaxStats = 40;

    private const int MaxKeyLength = 32;

    private const int MaxValueLength = 60;

    public static readonly IReadOnlyDictionary<string, string> None =
        new Dictionary<string, string>();

    /// <summary>JSON for the column, or null when there is nothing to say.</summary>
    public static string? Serialise(IReadOnlyDictionary<string, string>? stats)
    {
        if (stats is null || stats.Count == 0)
        {
            return null;
        }

        // Bounded on the way in. These values come from somebody else's server, and
        // they end up drawn onto an image and stored in a column.
        var tidy = stats
            .Where(s => s.Key.Length is > 0 and <= MaxKeyLength
                        && !string.IsNullOrWhiteSpace(s.Value))
            .Take(MaxStats)
            .ToDictionary(
                s => s.Key,
                s => s.Value.Length > MaxValueLength ? s.Value[..MaxValueLength] : s.Value,
                StringComparer.OrdinalIgnoreCase);

        return tidy.Count == 0 ? null : JsonSerializer.Serialize(tidy);
    }

    /// <summary>What the column holds, or nothing. Never throws on a bad document.</summary>
    public static IReadOnlyDictionary<string, string> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return None;
        }

        try
        {
            var read = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            return read is null
                ? None
                : new Dictionary<string, string>(read, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // A row written by an older shape, or by hand. A signature with one
            // empty token beats a signature that will not render.
            return None;
        }
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace Rmv.Web.Tools;

/// <summary>One player's roll, in the order it appeared in the log.</summary>
public sealed record Roll(int Value, string Who);

/// <summary>Everyone who rolled a given number, in log order.</summary>
public sealed record RollGroup(int Value, IReadOnlyList<string> Names);

public sealed record RollReport(
    IReadOnlyList<RollGroup> Groups,
    int LinesScanned,
    int RollsFound,
    bool HitLineLimit,
    bool HitRollLimit)
{
    public static readonly RollReport Empty = new([], 0, 0, false, false);

    /// <summary>Highest roll, or null if nothing parsed. Ties are all listed.</summary>
    public RollGroup? Winner => Groups.Count > 0 ? Groups[0] : null;

    public bool Truncated => HitLineLimit || HitRollLimit;
}

/// <summary>
/// Parses Dark Age of Camelot chat logs for /random results and groups them by
/// value, highest first.
///
/// Ported from a 2011 PHP script whose whole parser was this:
///
///     $regx = '|] (.*) picks? a random number between 1 and 100: (.*)\r|';
///
/// That is too loose to be a validator: `(.*)` accepts any bytes at all for both
/// the name and the value, and the PHP then echoed the name straight into HTML.
/// The regex here is anchored per line and bounds both captures, so anything not
/// shaped exactly like a roll line is ignored rather than sanitised after the
/// fact. Nothing from the file reaches output except a name matching
/// [A-Za-z]{1,24} and an integer 0-100.
/// </summary>
public static partial class RollParser
{
    /// <summary>
    /// Lines are cheap to skip but not free. A 2MB log is roughly 25k lines, so
    /// this is far above any real log and only guards against a pathological file.
    /// </summary>
    public const int MaxLines = 250_000;

    /// <summary>Bounds the size of the rendered result, not the parse.</summary>
    public const int MaxRolls = 20_000;

    // Anchored at both ends, every quantifier bounded.
    //
    //   [Sat Jan 01 12:00:00 2011] Playername picks a random number between 1 and 100: 87
    //   Playername picks a random number between 1 and 100: 87.
    //   You pick a random number between 1 and 100: 42
    //
    // The timestamp is optional because not every client writes one. "pick" and
    // "picks" both appear: the game uses "You pick" for your own roll. DAoC
    // character names are alphabetic only, which is what keeps the name capture
    // tight enough to render without escaping concerns.
    [GeneratedRegex(
        @"^(?:\[[^\]\r\n]{1,40}\]\s*)?(?<who>[A-Za-z]{1,24})\s+picks?\s+a\s+random\s+number\s+between\s+1\s+and\s+100:\s*(?<roll>\d{1,3})\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex RollLine();

    public static RollReport Parse(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // The log is whatever the game wrote, which is not always valid UTF-8.
        // Replacement characters are fine: they cannot match the pattern, so a
        // mis-decoded line is simply skipped rather than throwing.
        using var reader = new StreamReader(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);

        return Parse(reader);
    }

    public static RollReport Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        // Keyed by roll value. Insertion order within a value is log order, which
        // is what the original did and is what makes a reroll legible.
        var byValue = new Dictionary<int, List<string>>();
        var lines = 0;
        var rolls = 0;
        var hitLineLimit = false;
        var hitRollLimit = false;

        while (reader.ReadLine() is { } line)
        {
            if (lines >= MaxLines)
            {
                hitLineLimit = true;
                break;
            }

            lines++;

            var match = RollLine().Match(line);
            if (!match.Success)
            {
                continue;
            }

            // 1-3 digits, so this cannot overflow, but it can exceed 100.
            if (!int.TryParse(match.Groups["roll"].ValueSpan, out var value) || value is < 0 or > 100)
            {
                continue;
            }

            if (rolls >= MaxRolls)
            {
                hitRollLimit = true;
                break;
            }

            rolls++;

            var who = match.Groups["who"].Value;
            if (!byValue.TryGetValue(value, out var names))
            {
                names = [];
                byValue[value] = names;
            }

            names.Add(who);
        }

        var groups = byValue
            .OrderByDescending(kv => kv.Key)
            .Select(kv => new RollGroup(kv.Key, kv.Value))
            .ToList();

        return new RollReport(groups, lines, rolls, hitLineLimit, hitRollLimit);
    }
}

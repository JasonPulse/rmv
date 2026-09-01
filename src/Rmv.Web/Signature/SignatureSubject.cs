using System.Globalization;
using Rmv.Web.Data;

namespace Rmv.Web.Signature;

/// <summary>
/// One character, as a signature draws it.
///
/// Strings, not numbers: everything here is going into a template, and the
/// formatting of a number is part of what it says. v1's herald handed out
/// "1,234,567" already grouped and that is what people expect to see in a
/// signature, so grouping happens here rather than in twelve template strings.
/// </summary>
public sealed record SignatureCharacter(
    string Name,
    string Level,
    string Class,
    string Race,
    string Realm,
    string Guild,
    string Rank,
    string Score,
    string Kills,
    string Deaths,
    string Game,
    string Seen,
    IReadOnlyDictionary<string, string> Stats);

/// <summary>
/// What a member adds up to across every herald, which is the part v1 could not do:
/// it only knew the characters named in one URL, all on one server.
/// </summary>
public sealed record SignatureTotals(
    string Characters,
    string Games,
    string Levels,
    string Score,
    string Kills,
    string Since);

/// <summary>Everything a template can draw on, for one element.</summary>
/// <param name="Character">
/// Null for an element bound to nobody, which is how a line of pure member totals
/// works. A character token on such an element resolves to nothing rather than
/// failing the render, and so does a herald's own stat.
/// </param>
public sealed record SignatureSubject(
    string User,
    SignatureTotals Totals,
    SignatureCharacter? Character);

/// <summary>
/// Turns rows into what a signature can say.
///
/// One place, because the editor's preview, the renderer and the tests all have to
/// agree about what %Score% means. Everything a herald gives us is already on
/// Character; the totals are the sum across a member's whole roster whatever game
/// each row came from.
/// </summary>
public static class SignatureData
{
    /// <summary>The subject for one element, bound to a character or to nobody.</summary>
    public static SignatureSubject Subject(
        Member member, IReadOnlyList<Character> roster, int? characterId)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(roster);

        var bound = characterId is { } id ? roster.FirstOrDefault(c => c.Id == id) : null;

        return new SignatureSubject(member.Handle, Totals(roster), bound is null ? null : Of(bound));
    }

    public static SignatureCharacter Of(Character c)
    {
        ArgumentNullException.ThrowIfNull(c);

        return new SignatureCharacter(
            Name: c.Name,
            Level: Number(c.Level),
            Class: Text(c.Class),
            Race: Text(c.Race),
            Realm: Text(c.Realm),
            Guild: Text(c.Guild),
            Rank: Text(c.RealmRank),
            Score: Number(c.Score),
            Kills: Number(c.Kills),
            Deaths: Number(c.Deaths),
            Game: Text(c.Game?.Game),
            Seen: Text(c.LastOnline),
            // Whatever this character's own herald publishes. Already formatted by
            // the adapter that knows what its numbers mean.
            Stats: Herald.HeraldStats.Read(c.Stats));
    }

    /// <summary>
    /// The cross-herald sums. Realm points on DAoC, total job levels on FFXI and
    /// achievement points on WoW all live in Character.Score, so adding them is
    /// adding numbers that mean different things. That is the joke, and it is what
    /// he asked for: one number for "how much of this have I done".
    /// </summary>
    public static SignatureTotals Totals(IReadOnlyList<Character> roster)
    {
        ArgumentNullException.ThrowIfNull(roster);

        var oldest = roster.Count == 0 ? (DateTimeOffset?)null : roster.Min(c => c.AddedAt);

        return new SignatureTotals(
            Characters: Number(roster.Count),
            Games: Number(roster.Select(c => c.GamePresenceId).Distinct().Count()),
            Levels: Number(roster.Sum(c => (long?)c.Level ?? 0)),
            Score: Number(roster.Sum(c => c.Score ?? 0)),
            Kills: Number(roster.Sum(c => c.Kills ?? 0)),
            Since: oldest is { } when
                ? when.Year.ToString(CultureInfo.InvariantCulture)
                : "");
    }

    /// <summary>Grouped, as a herald writes it. Absent is empty, never "0".</summary>
    private static string Number(long? value) =>
        value is { } n ? n.ToString("N0", CultureInfo.InvariantCulture) : "";

    /// <summary>A year is not grouped, and neither is anything else four digits wide.</summary>
    private static string Text(string? value) => (value ?? "").Trim();
}

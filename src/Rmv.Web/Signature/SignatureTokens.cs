using System.Text;

namespace Rmv.Web.Signature;

/// <summary>Where a token gets its value, which decides where the editor offers it.</summary>
public enum TokenScope
{
    /// <summary>The element's own character. Nothing to draw on an unbound element.</summary>
    Character,

    /// <summary>The member, across every herald. Available on every element.</summary>
    Member,

    /// <summary>Neither: a fixed string, kept because v1 had it.</summary>
    Fixed,
}

/// <param name="Name">Without the percent signs.</param>
/// <param name="Example">What it looks like filled in, for the editor's palette.</param>
public sealed record SignatureToken(
    string Name,
    TokenScope Scope,
    string Description,
    string Example,
    Func<SignatureSubject, string> Value);

/// <summary>
/// Every token a signature template can contain, once.
///
/// The resolver reads this list and so will the editor's palette, because a token
/// the editor offers and the renderer does not know is a signature that quietly
/// says "%Realm%" to a forum. One list, one meaning each.
///
/// The names are readable where v1's were two letters: %C, %AL, %TRL and the rest
/// were unguessable, and there is nothing to stay compatible with because the old
/// designs lived in a MySQL table that is not in the backup. The one exception is
/// %SP%, which was a genuine convenience and costs a line to keep.
///
/// v1's %JT, the justify break, is deliberately not here. It split a line into two
/// halves aligned on the longest left part, which was the only way to line up a
/// label and a value inside a fixed grid of twelve slots. Positioning two elements
/// does that better, and this model has positioning.
///
/// Every herald's own stats are on top of this list rather than in it. Each adapter
/// declares what it publishes and fills it per character, and Resolve falls back to
/// the bound character's; see IHeraldAdapter.Stats. That is what makes one signature
/// able to carry a DAoC line about relics, an FFXI line about master level and a
/// total across both.
/// </summary>
public static class SignatureTokens
{
    public static readonly IReadOnlyList<SignatureToken> All =
    [
        // --- the element's character -----------------------------------------
        new("Name", TokenScope.Character, "Character name", "Property",
            s => s.Character?.Name ?? ""),
        new("Level", TokenScope.Character, "Level", "50",
            s => s.Character?.Level ?? ""),
        new("Class", TokenScope.Character, "Class, job or specialisation", "Skald",
            s => s.Character?.Class ?? ""),
        new("Race", TokenScope.Character, "Race", "Norseman",
            s => s.Character?.Race ?? ""),
        new("Realm", TokenScope.Character, "Realm, server or faction", "Midgard",
            s => s.Character?.Realm ?? ""),
        new("Guild", TokenScope.Character, "Guild", "Results May Vary",
            s => s.Character?.Guild ?? ""),
        new("Rank", TokenScope.Character, "Realm rank or title", "8L0",
            s => s.Character?.Rank ?? ""),
        new("Score", TokenScope.Character, "Realm points, job levels or achievement points",
            "1,234,567", s => s.Character?.Score ?? ""),
        new("Kills", TokenScope.Character, "Kills", "12,345",
            s => s.Character?.Kills ?? ""),
        new("Deaths", TokenScope.Character, "Deaths", "678",
            s => s.Character?.Deaths ?? ""),
        new("Game", TokenScope.Character, "The game this character is on", "Dark Age of Camelot",
            s => s.Character?.Game ?? ""),
        new("Seen", TokenScope.Character, "When the herald last saw them", "2026-05-01",
            s => s.Character?.Seen ?? ""),

        // --- the member, across every herald ----------------------------------
        new("User", TokenScope.Member, "Your name here", "Property",
            s => s.User),
        new("AllChars", TokenScope.Member, "How many characters you have added", "6",
            s => s.Totals.Characters),
        new("AllGames", TokenScope.Member, "How many games those span", "4",
            s => s.Totals.Games),
        new("AllLevels", TokenScope.Member, "Every level, added up", "312",
            s => s.Totals.Levels),
        new("AllScore", TokenScope.Member, "Every score, added up, whatever it measures",
            "2,345,678", s => s.Totals.Score),
        new("AllKills", TokenScope.Member, "Every kill, added up", "23,456",
            s => s.Totals.Kills),
        new("Since", TokenScope.Member, "The year of your oldest character here", "2001",
            s => s.Totals.Since),

        // --- v1 ---------------------------------------------------------------
        // Its own scope so the editor labels it "Separator" rather than lumping it in
        // with the rest. The description says what it puts in, because nobody is
        // going to click an unexplained token to find out.
        new("SP", TokenScope.Fixed, "Puts \" - \" in, to separate two things", " - ", _ => " - "),
    ];

    private static readonly Dictionary<string, SignatureToken> ByName =
        All.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

    public static SignatureToken? Find(string name) => ByName.GetValueOrDefault(name);

    /// <summary>
    /// Fills a template in.
    ///
    /// %% is a literal percent. An unknown token is left exactly as typed, because a
    /// mistyped %Realmm% showing up in the signature is how somebody notices; the
    /// alternative is a gap they have to work out for themselves.
    ///
    /// A token that resolves to nothing leaves nothing, which is v1's behaviour and
    /// is what makes "%Guild%" on a guildless character render as an empty space
    /// rather than the word null.
    /// </summary>
    public static string Resolve(string? template, SignatureSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        if (string.IsNullOrEmpty(template))
        {
            return "";
        }

        var text = new StringBuilder(template.Length);
        var i = 0;

        while (i < template.Length)
        {
            if (template[i] != '%')
            {
                text.Append(template[i++]);
                continue;
            }

            // %% is one percent.
            if (i + 1 < template.Length && template[i + 1] == '%')
            {
                text.Append('%');
                i += 2;
                continue;
            }

            var close = template.IndexOf('%', i + 1);
            if (close < 0)
            {
                // A percent with no partner is just a percent.
                text.Append(template[i..]);
                break;
            }

            var name = template[(i + 1)..close];

            if (Find(name) is { } token)
            {
                text.Append(token.Value(subject));
            }
            else if (subject.Character?.Stats.TryGetValue(name, out var stat) == true)
            {
                // The bound character's own herald publishes this one. Checked after
                // the shared tokens, so no herald can shadow %Name%.
                text.Append(stat);
            }
            else if (Herald.HeraldStatTokens.IsKnown(name))
            {
                // A real token, from a herald this line's character is not on. Empty
                // rather than left as typed: "%Relics%" appearing on an FFXI line is
                // not a typo to point out, it is a stat that does not apply.
            }
            else
            {
                // Left as typed, including the percent signs, so a mistyped
                // %Realmm% is visible rather than a gap somebody has to work out.
                text.Append(template[i..(close + 1)]);
            }

            i = close + 1;
        }

        return text.ToString();
    }
}

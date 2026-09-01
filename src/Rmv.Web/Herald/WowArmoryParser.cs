using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Rmv.Web.Herald;

/// <summary>
/// Reads a character out of an Armory page.
///
/// The Armory is a JavaScript application, but it ships the whole character as JSON
/// in the page it serves, in a script that reads
/// <c>var characterProfileInitialState = {...}</c>. So there is no headless browser
/// here and no Blizzard API credential: one GET, one JSON parse. That JSON is what
/// the page's own React app renders from, which makes it as authoritative as
/// anything on the site.
///
/// Nothing here is a DOM query, unlike the other three heralds, because nothing
/// needs to be. If Blizzard ever renames that variable this parser reports "not a
/// character page" rather than half a character, which is the failure worth having.
/// </summary>
public static class WowArmoryParser
{
    private const string Marker = "characterProfileInitialState";

    /// <summary>
    /// The JSON object assigned to characterProfileInitialState, or null.
    ///
    /// Brace counting alone is not enough and this page proves it: a character's
    /// title is stored as "Inquisitor {name}", so a naive scan closes the object
    /// early and hands back invalid JSON. String literals and their escapes are
    /// skipped for that reason.
    /// </summary>
    public static string? ExtractState(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        var at = html.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var open = html.IndexOf('{', at);
        if (open < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = open; i < html.Length; i++)
        {
            var ch = html[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return html[open..(i + 1)];
                    }

                    break;
            }
        }

        // Unbalanced, which means the page was truncated. Better nothing than a
        // partial parse.
        return null;
    }

    /// <summary>
    /// The character, or null when the page is not a character profile.
    ///
    /// Every field is optional except the name, on the same grounds as the other
    /// adapters: Blizzard moves things, and a parser that insists on everything
    /// fails completely when one key is renamed.
    /// </summary>
    public static HeraldCharacter? Parse(string html, string url)
    {
        if (ExtractState(html) is not { } json)
        {
            return null;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("character", out var c)
                || c.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var name = Text(c, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            return new HeraldCharacter
            {
                Name = name,
                Realm = Where(c),
                Class = Specialisation(c),
                Race = Named(c, "race"),
                Level = Number(c, "level") is { } level and > 0 and < 1000 ? (int)level : null,
                Guild = Text(Child(c, "guild"), "name"),
                // The title the character is wearing, which is what RealmRank holds
                // for FFXI as well. Stored as "Inquisitor {name}", so the prefix is
                // the part worth keeping.
                RealmRank = Text(c, "prefix"),
                // Achievement points: cumulative, never goes down, and the closest
                // thing WoW has to FFXI's total job levels. Item level is deliberately
                // not used here; it measures current gear, not what anyone has done.
                RealmPoints = Number(c, "achievement"),
                Kills = Number(Child(Child(c, "pvp"), "honorableKills"), "value"),
                LastOnline = LastUpdated(c),
                Url = url,
                Portrait = Portrait(c),
                Stats = Stats(document.RootElement, c),
            };
        }
    }

    /// <summary>
    /// What the Armory publishes that the shared fields have no room for.
    ///
    /// Item level in particular: it is the number WoW players compare, and it is
    /// deliberately not %Score%, which ranks a board spanning four games.
    /// </summary>
    private static Dictionary<string, string> Stats(JsonElement root, JsonElement c)
    {
        var stats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Count(string key, long? value)
        {
            if (value is { } n && n > 0)
            {
                stats[key] = n.ToString("N0", CultureInfo.InvariantCulture);
            }
        }

        Count("ItemLevel", Number(c, "averageItemLevel"));
        Count("Honor", Number(Child(Child(c, "pvp"), "prestige"), "honorLevel"));
        Count("Renown", Number(c, "renown"));

        // The mythic rating lives in the summary rather than on the character, and
        // only for somebody who runs them.
        Count("Mythic", Number(Child(Child(root, "summary"), "mythicKeystoneDungeons"), "rating")
                        ?? Number(Child(root, "summary"), "mythicRating"));

        foreach (var (key, from) in new[] { ("Spec", "spec"), ("Faction", "faction"), ("Server", "realm") })
        {
            if (Named(c, from) is { Length: > 0 } value)
            {
                stats[key] = value;
            }
        }

        return stats;
    }

    /// <summary>
    /// Server and faction together, because the card has one field for where
    /// somebody plays and both halves of that answer matter in a two-faction game.
    /// </summary>
    private static string? Where(JsonElement c)
    {
        var realm = Named(c, "realm");
        var faction = Named(c, "faction");

        return (realm, faction) switch
        {
            (null, null) => null,
            (null, _) => faction,
            (_, null) => realm,
            _ => $"{realm} ({faction})",
        };
    }

    /// <summary>
    /// "Frost Death Knight" rather than "Death Knight", because the specialisation
    /// is what anyone means when they ask what someone plays.
    /// </summary>
    private static string? Specialisation(JsonElement c)
    {
        var klass = Named(c, "class");
        var spec = Named(c, "spec");

        if (string.IsNullOrWhiteSpace(klass))
        {
            return spec;
        }

        return string.IsNullOrWhiteSpace(spec) ? klass : $"{spec} {klass}";
    }

    /// <summary>
    /// The full body render, versioned by when Blizzard last rebuilt the character.
    ///
    /// The URL alone is not a version: it carries the character id, not the
    /// appearance, so a new set of armour reuses it and the stored picture would
    /// never be refetched. The update timestamp is exactly when the render changes,
    /// which is what HeraldPortrait.Version is for.
    /// </summary>
    private static HeraldPortrait? Portrait(JsonElement c)
    {
        var url = Text(Child(c, "renderRaw"), "url")
                  ?? Text(Child(c, "bust"), "url")
                  ?? Text(Child(c, "avatar"), "url");

        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var stamp = Number(Child(c, "lastUpdatedTimestamp"), "epoch");

        return new HeraldPortrait(url);
    }

    /// <summary>
    /// The date Blizzard last refreshed the character, which is as close as the
    /// Armory comes to saying when it was last played.
    /// </summary>
    private static string? LastUpdated(JsonElement c)
    {
        var stamp = Child(c, "lastUpdatedTimestamp");

        if (Text(stamp, "iso8601") is { Length: > 0 } iso
            && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out var when))
        {
            return when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return Number(stamp, "epoch") is { } epoch and > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;
    }

    // --- reading the payload without trusting its shape ------------------------

    private static JsonElement Child(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var child)
            ? child
            : default;

    /// <summary>Most of this payload is {"name": "...", "slug": "..."} objects.</summary>
    private static string? Named(JsonElement parent, string name) => Text(Child(parent, name), "name");

    private static string? Text(JsonElement parent, string name) =>
        Child(parent, name) is { ValueKind: JsonValueKind.String } value
        && value.GetString() is { Length: > 0 } text
            ? Tidy(text)
            : null;

    private static long? Number(JsonElement parent, string name) =>
        Child(parent, name) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt64(out var number)
            ? number
            : null;

    /// <summary>
    /// Collapses whitespace and drops the title's {name} placeholder, so a stored
    /// value never contains the template Blizzard renders around the character.
    /// </summary>
    private static string Tidy(string text)
    {
        var cleaned = text.Replace("{name}", "", StringComparison.Ordinal);
        var sb = new StringBuilder(cleaned.Length);
        var space = false;

        foreach (var ch in cleaned)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }
}

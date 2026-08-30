using System.Globalization;
using System.Text;

namespace Rmv.Web.Herald;

/// <summary>
/// World of Warcraft, via Blizzard's Armory.
///
/// No API credential. The Armory page carries the whole character as JSON, so this
/// is one GET and a parse; see WowArmoryParser.
///
/// Two things make this herald unlike the other three.
///
/// A name is not enough. WoW names are unique per realm, not per game, so
/// "Syfr" identifies nobody on its own. This takes a pasted Armory address, or
/// "realm/name", or the in-game "Name-Realm", and asks for one of those when given
/// a bare name rather than guessing a realm.
///
/// It does not list everyone. The Armory only shows characters on an account with
/// an active subscription, and a lapsed account answers exactly the same as a
/// misspelling: HTTP 500 with no character in the page. That is what CoverageNote
/// exists for, and why a member may type a WoW character in by hand instead.
/// </summary>
public sealed class WowArmoryAdapter(HeraldFetcher fetcher) : IHeraldAdapter
{
    public string Key => "armory";

    public string DisplayName => "WoW Armory";

    public string DefaultBaseUrl => "https://worldofwarcraft.blizzard.com";

    /// <summary>
    /// Achievement points. Cumulative and never falls, which is what a board
    /// covering years should rank on; see WowArmoryParser.
    /// </summary>
    public LeaderboardMetric Metric => new(RankBy.Score, "Achievement points");

    public string? CoverageNote =>
        "The Armory only lists characters on an account with an active "
        + "subscription. A lapsed account looks the same as a wrong name.";

    /// <summary>
    /// The locale segment. Ours, not the member's: the payload this reads is the
    /// same in every locale, and pinning it keeps the URL predictable.
    /// </summary>
    private const string Locale = "en-us";

    /// <summary>
    /// Regions the Armory serves under this address. China is a separate site
    /// entirely, so it is not one of them.
    /// </summary>
    private static readonly string[] Regions = ["us", "eu", "kr", "tw"];

    private const string DefaultRegion = "us";

    public async Task<HeraldResult> FetchCharacterAsync(
        string baseUrl, string characterName, CancellationToken ct)
    {
        if (!Data.ExternalUrl.TryParse(baseUrl, out var root))
        {
            return HeraldResult.Fail("That herald address does not look right.");
        }

        var typed = (characterName ?? "").Trim();
        if (typed.Length == 0)
        {
            return HeraldResult.Fail("Enter a character name.");
        }

        if (Identify(typed) is not { } who)
        {
            return HeraldResult.Fail(
                "The Armory needs the realm as well as the name, because two "
                + "characters on different realms can share one name. Give it as "
                + "\"Syfr-Quel'Thalas\", or paste the address of the Armory page.");
        }

        var url = CharacterUrl(root, who);

        var fetched = await fetcher.GetAsync(url, ct);

        if (!fetched.Ok)
        {
            // The Armory answers 500, not 404, for a character it will not show, so
            // a server error here is the ordinary case rather than an outage. Both
            // possible reasons are given because they are indistinguishable from
            // outside.
            return fetched.StatusCode is >= 400 and < 600
                ? HeraldResult.Fail(
                    $"The Armory has no character called \"{who.Name}\" on {who.Realm}. "
                    + "Check the spelling and the realm. " + CoverageNote)
                : HeraldResult.Fail(fetched.Error ?? "Could not reach the Armory.");
        }

        var character = WowArmoryParser.Parse(fetched.Body!, url);

        return character is null
            ? HeraldResult.Fail("That Armory page does not contain a character.")
            : HeraldResult.Found(character);
    }

    /// <summary>Where a character lives, in the three parts the URL needs.</summary>
    /// <param name="Name">As typed, for messages. The URL uses the slug.</param>
    public sealed record Who(string Region, string Realm, string Name);

    /// <summary>
    /// Public so the page and the tests can build the same address this fetches.
    /// </summary>
    public static string CharacterUrl(string root, Who who) =>
        $"{root.TrimEnd('/')}/{Locale}/worldsoul/{who.Region}/armory/character/"
        + $"{Uri.EscapeDataString(Slug(who.Realm))}/{Uri.EscapeDataString(Slug(who.Name))}";

    /// <summary>
    /// Works out which character was meant, or null when the realm is missing.
    ///
    /// Three forms, because members will use all three: the address of the page
    /// they are looking at, the "Name-Realm" they see in game, and "realm/name"
    /// from the URL they half remember.
    /// </summary>
    public static Who? Identify(string typed)
    {
        var text = (typed ?? "").Trim();
        if (text.Length == 0)
        {
            return null;
        }

        return FromUrl(text) ?? FromRealmSlashName(text) ?? FromNameDashRealm(text);
    }

    /// <summary>
    /// Whether a fragment could be a realm or a character name.
    ///
    /// Checked in every form, and the reason is a real failure: an Armory guild
    /// address matches no character shape, so it fell through to "Name-Realm",
    /// which split it on the last hyphen it could find. That hyphen was the one in
    /// "en-us", and the result was a request for a character named after half a URL.
    ///
    /// Neither a realm nor a character name contains punctuation that belongs to a
    /// URL, so that is what this rules out.
    /// </summary>
    private static readonly System.Buffers.SearchValues<char> UrlPunctuation =
        System.Buffers.SearchValues.Create("/:.?#@&=");

    private static bool Plausible(string part) =>
        part.Length is > 0 and <= 48 && !part.AsSpan().ContainsAny(UrlPunctuation);

    private static Who? Checked(Who who) =>
        Plausible(who.Realm) && Plausible(who.Name) && Slug(who.Realm).Length > 0
        && Slug(who.Name).Length > 0
            ? who
            : null;

    /// <summary>
    /// A pasted Armory address, in either shape Blizzard has used.
    ///
    /// Current: /en-us/worldsoul/us/armory/character/quelthalas/syfr
    /// Older:   /en-us/character/us/quelthalas/syfr
    ///
    /// The region comes from the address, so an EU character pasted by a member
    /// resolves against the EU Armory without anyone choosing a setting.
    /// </summary>
    private static Who? FromUrl(string text)
    {
        if (!text.Contains("//", StringComparison.Ordinal)
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var parts = uri.AbsolutePath
            .Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var at = Array.FindIndex(parts, p => p.Equals("character", StringComparison.OrdinalIgnoreCase));
        if (at < 0 || parts.Length < at + 3)
        {
            return null;
        }

        // Either the two segments after "character" are realm and name, or the
        // older form puts the region there first.
        var rest = parts[(at + 1)..];

        var region = Regions.FirstOrDefault(r => r.Equals(rest[0], StringComparison.OrdinalIgnoreCase))
                     ?? Regions.FirstOrDefault(r => Array.Exists(parts[..at],
                         p => p.Equals(r, StringComparison.OrdinalIgnoreCase)))
                     ?? DefaultRegion;

        // Drop the region segment when it is the one immediately after "character".
        if (rest[0].Equals(region, StringComparison.OrdinalIgnoreCase) && rest.Length >= 3)
        {
            rest = rest[1..];
        }

        return rest.Length >= 2 && rest[0].Length > 0 && rest[1].Length > 0
            ? Checked(new Who(region.ToLowerInvariant(), Unslug(rest[0]), Unslug(rest[1])))
            : null;
    }

    /// <summary>"quelthalas/syfr", which is what the URL reads.</summary>
    private static Who? FromRealmSlashName(string text)
    {
        var parts = text.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return parts.Length == 2
            ? Checked(new Who(DefaultRegion, parts[0], parts[1]))
            : null;
    }

    /// <summary>
    /// "Syfr-Quel'Thalas", which is how the game itself names a character from
    /// another realm.
    ///
    /// Split on the first hyphen, not the last, because a character name cannot
    /// contain one and a realm can: "Syfr-Emerald-Dream" is Syfr on Emerald Dream,
    /// and splitting at the end would look for a character called Syfr-Emerald on a
    /// realm called Dream.
    /// </summary>
    private static Who? FromNameDashRealm(string text)
    {
        var at = text.IndexOf('-', StringComparison.Ordinal);

        if (at <= 0 || at == text.Length - 1)
        {
            return null;
        }

        var name = text[..at].Trim();
        var realm = text[(at + 1)..].Trim();

        return Checked(new Who(DefaultRegion, realm, name));
    }

    /// <summary>
    /// A realm or character name as the Armory writes it in a URL.
    ///
    /// Apostrophes come out, spaces become hyphens, and accents lose their marks:
    /// "Quel'Thalas" is quelthalas, "Aerie Peak" is aerie-peak and "Área 52" is
    /// area-52. Blizzard's own slugs, which is why this is not a generic slugify.
    /// </summary>
    public static string Slug(string value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        var stripped = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(stripped.Length);

        foreach (var ch in stripped)
        {
            switch (CharUnicodeInfo.GetUnicodeCategory(ch))
            {
                case UnicodeCategory.NonSpacingMark:
                    continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (ch is ' ' or '_' or '-')
            {
                // One hyphen, never two, and never a trailing one.
                if (sb.Length > 0 && sb[^1] != '-')
                {
                    sb.Append('-');
                }
            }

            // Everything else, apostrophes included, is dropped.
        }

        return sb.ToString().Trim('-').Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// A slug back into something readable, for the message a member sees when a
    /// lookup fails. "aerie-peak" becomes "Aerie Peak"; an apostrophe that the slug
    /// dropped stays dropped, because guessing where it went would be worse.
    /// </summary>
    private static string Unslug(string slug)
    {
        var words = slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 1 ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..]);

        return string.Join(' ', words);
    }
}

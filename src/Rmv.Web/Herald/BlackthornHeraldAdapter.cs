using AngleSharp;
using AngleSharp.Dom;

namespace Rmv.Web.Herald;

/// <summary>
/// Blackthorn DAoC. Server-rendered HTML: a character page is a stack of small
/// tables, each with its own heading.
///
/// Parsed by label rather than by position. The page has eight tables and the
/// interesting values sit in different ones, so indexing into "table 4, row 2"
/// would break the moment a section is added above it. Finding the table whose
/// heading reads "LVL" survives that.
/// </summary>
public sealed class BlackthornHeraldAdapter(HeraldFetcher fetcher) : IHeraldAdapter
{
    public string Key => "blackthorn";

    public string DisplayName => "Blackthorn DAoC";

    public string DefaultBaseUrl => "https://herald.blackthorn-daoc.com";

    public async Task<HeraldResult> FetchCharacterAsync(
        string baseUrl, string characterName, CancellationToken ct)
    {
        if (!TryBuildUrl(baseUrl, characterName, out var url))
        {
            return HeraldResult.Fail("That herald address does not look right.");
        }

        var (body, failure) = await fetcher.GetForCharacterAsync(url, characterName, ct);
        if (body is null)
        {
            return failure!;
        }

        var context = BrowsingContext.New(AngleSharp.Configuration.Default);
        var doc = await context.OpenAsync(req => req.Content(body), ct);

        // The herald answers 200 with a shell for a name it does not know, so the
        // title is what actually confirms the character exists.
        var title = doc.Title ?? "";
        if (!title.Contains(characterName, StringComparison.OrdinalIgnoreCase))
        {
            return HeraldResult.Fail($"The herald has no character called \"{characterName}\".");
        }

        return HeraldResult.Found(BlackthornParser.Parse(characterName, doc, url));
    }

    public static bool TryBuildUrl(string baseUrl, string characterName, out string url)
    {
        url = "";

        if (!Data.ExternalUrl.TryParse(baseUrl, out var root))
        {
            return false;
        }

        // Names are alphabetic on DAoC servers. Refusing anything else keeps
        // path traversal and query injection out of the URL entirely.
        if (!IsPlausibleCharacterName(characterName))
        {
            return false;
        }

        url = $"{root.TrimEnd('/')}/stats/player/{Uri.EscapeDataString(characterName)}";
        return true;
    }

    public static bool IsPlausibleCharacterName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 24
        && name.All(char.IsAsciiLetter);
}

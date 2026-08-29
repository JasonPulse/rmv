using AngleSharp;
using AngleSharp.Dom;

namespace Rmv.Web.Herald;

/// <summary>
/// Final Fantasy XIV, via Square Enix's Lodestone.
///
/// The Lodestone is keyed by a numeric character id, not by name, and a name is
/// only unique within one world. So a typed name goes through the site's own
/// search first. One match is used; several are reported back with their worlds
/// rather than guessed at, because picking the wrong Aoii would attach a stranger
/// to someone's profile. A pasted character URL or a bare id skips the search
/// entirely, which is what anyone looking at their own Lodestone page will do.
///
/// Two requests per character: the profile, then the Class/Job page. The active
/// job's name exists nowhere as text on the profile, only as an image, and the
/// Class/Job page is what turns that image back into "White Mage". See
/// LodestoneParser.ActiveJobIcon.
/// </summary>
public sealed class LodestoneAdapter(HeraldFetcher fetcher) : IHeraldAdapter
{
    public string Key => "lodestone";

    public string DisplayName => "FFXIV Lodestone";

    public string DefaultBaseUrl => "https://na.finalfantasyxiv.com";

    /// <summary>
    /// The active job's level. The Lodestone publishes no cumulative number, so
    /// there is nothing to put in Score and nothing else to rank on.
    /// </summary>
    public LeaderboardMetric Metric => new(RankBy.Level, "Level");

    public async Task<HeraldResult> FetchCharacterAsync(
        string baseUrl, string characterName, CancellationToken ct)
    {
        if (!Data.ExternalUrl.TryParse(baseUrl, out var root))
        {
            return HeraldResult.Fail("That herald address does not look right.");
        }

        root = root.TrimEnd('/');

        var typed = (characterName ?? "").Trim();
        if (typed.Length == 0)
        {
            return HeraldResult.Fail("Enter a character name.");
        }

        var id = LodestoneParser.IdFromHref(typed) ?? BareId(typed);
        if (id is null)
        {
            var found = await ResolveByNameAsync(root, typed, ct);
            if (found.Error is not null)
            {
                return HeraldResult.Fail(found.Error);
            }

            id = found.Id;
        }

        return await FetchByIdAsync(root, id!.Value, ct);
    }

    /// <summary>Public so the character page can be linked without a fetch.</summary>
    public static string CharacterUrl(string root, long id) =>
        $"{root.TrimEnd('/')}/lodestone/character/{id}/";

    private async Task<HeraldResult> FetchByIdAsync(string root, long id, CancellationToken ct)
    {
        var url = CharacterUrl(root, id);

        var profile = await fetcher.GetAsync(url, ct);
        if (!profile.Ok)
        {
            return profile.NotFound
                ? HeraldResult.Fail("The Lodestone has no character with that id.")
                : HeraldResult.Fail(profile.Error ?? "Could not reach the Lodestone.");
        }

        var doc = await ParseAsync(profile.Body!, ct);

        // The Lodestone answers 200 with its own error page for a deleted
        // character, so the name is what confirms the profile is real.
        if (doc.QuerySelector(".frame__chara__name") is null)
        {
            return HeraldResult.Fail("That Lodestone page is not a character profile.");
        }

        // A missing Class/Job page costs the job name and nothing else, so it is
        // not worth failing the whole add over.
        var jobs = await fetcher.GetAsync(url + "class_job/", ct);
        var jobDoc = jobs.Ok ? await ParseAsync(jobs.Body!, ct) : null;

        return HeraldResult.Found(LodestoneParser.Parse(doc, jobDoc, url));
    }

    private async Task<(long? Id, string? Error)> ResolveByNameAsync(
        string root, string name, CancellationToken ct)
    {
        // The search is a GET with the name in the query, so it is escaped rather
        // than validated against a character set: FFXIV names contain spaces,
        // apostrophes and hyphens, and rejecting those would rule out most Elezen.
        var url = $"{root}/lodestone/character/?q={Uri.EscapeDataString(name)}";

        var fetched = await fetcher.GetAsync(url, ct);
        if (!fetched.Ok)
        {
            return (null, fetched.Error ?? "Could not reach the Lodestone.");
        }

        var doc = await ParseAsync(fetched.Body!, ct);
        var matches = LodestoneParser.Search(doc);

        // The search is a prefix-and-substring match, so "Aoii" can return
        // "Aoii Aeredel" and "Aoiix Something". An exact name is preferred before
        // anything is called ambiguous.
        var exact = matches
            .Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var candidates = exact.Count > 0 ? exact : matches;

        return candidates.Count switch
        {
            0 => (null, $"The Lodestone has no character called \"{name}\"."),
            1 => (candidates[0].Id, null),
            _ => (null, Ambiguous(name, candidates)),
        };
    }

    /// <summary>
    /// Names the worlds instead of picking one. The fix is for the member to paste
    /// their Lodestone URL, so the message says that.
    /// </summary>
    private static string Ambiguous(string name, IReadOnlyList<LodestoneMatch> matches)
    {
        var worlds = matches
            .Select(m => m.World)
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        var where = worlds.Count > 0 ? $" Found on {string.Join(", ", worlds)}." : "";

        return $"More than one character is called \"{name}\".{where} "
               + "Paste the address of your Lodestone page instead of the name.";
    }

    /// <summary>A number on its own is a character id, which is what the URL uses.</summary>
    private static long? BareId(string typed) =>
        typed.Length is > 0 and <= 12 && typed.All(char.IsAsciiDigit)
        && long.TryParse(typed, out var id) && id > 0
            ? id
            : null;

    private static async Task<IDocument> ParseAsync(string html, CancellationToken ct)
    {
        var context = BrowsingContext.New(AngleSharp.Configuration.Default);
        return await context.OpenAsync(req => req.Content(html), ct);
    }
}

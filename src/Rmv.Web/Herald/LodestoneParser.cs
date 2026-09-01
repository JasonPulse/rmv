using AngleSharp.Dom;

namespace Rmv.Web.Herald;

/// <summary>One row of the Lodestone name search.</summary>
public sealed record LodestoneMatch(long Id, string Name, string? World);

/// <summary>One entry from the Class/Job page.</summary>
public sealed record LodestoneJob(string Name, int? Level, string IconUrl);

/// <summary>
/// Parsing for the FFXIV Lodestone, separated from the adapter so tests run
/// against saved pages rather than Square Enix's servers.
///
/// Selected by class name throughout. The Lodestone's markup is generated from a
/// template and the class names are part of that template, so they survive a
/// content change in a way that "third div in the sidebar" does not.
/// </summary>
public static class LodestoneParser
{
    /// <summary>
    /// The active job's name is not text anywhere on the character page. It is a
    /// 266x28 PNG of the job title, and the small icon beside it has an empty alt.
    ///
    /// So the icon URL is the join key: the same asset appears on the Class/Job
    /// page, next to the job's name in plain text. Matching on it names the active
    /// job exactly, with no hardcoded table of jobs to fall out of date, and no
    /// guessing from levels when two jobs happen to be equal.
    /// </summary>
    public static string? ActiveJobIcon(IDocument doc) =>
        doc.QuerySelector(".character__class_icon img")?.GetAttribute("src");

    public static int? ActiveLevel(IDocument doc)
    {
        // "LEVEL 60", with trailing whitespace in the template.
        var text = Text(doc.QuerySelector(".character__class__data p"));
        return text is null ? null : FirstNumber(text);
    }

    public static IReadOnlyList<LodestoneJob> Jobs(IDocument doc)
    {
        var jobs = new List<LodestoneJob>();

        foreach (var li in doc.QuerySelectorAll(".character__job li"))
        {
            var name = Text(li.QuerySelector(".character__job__name"));
            var icon = li.QuerySelector(".character__job__icon img")?.GetAttribute("src");

            if (name is null || icon is null)
            {
                continue;
            }

            // An unlevelled job reads "-", which is not zero and is not a level.
            var level = FirstNumber(Text(li.QuerySelector(".character__job__level")) ?? "");
            jobs.Add(new LodestoneJob(name, level, icon));
        }

        return jobs;
    }

    /// <summary>
    /// The active job, found by icon. Falls back to the highest level, because a
    /// character with no job equipped still has a best job worth showing.
    /// </summary>
    public static LodestoneJob? PickJob(IDocument character, IReadOnlyList<LodestoneJob> jobs)
    {
        if (jobs.Count == 0)
        {
            return null;
        }

        var icon = ActiveJobIcon(character);
        var active = icon is null
            ? null
            : jobs.FirstOrDefault(j => string.Equals(j.IconUrl, icon, StringComparison.Ordinal));

        return active ?? jobs.Where(j => j.Level is not null).MaxBy(j => j.Level)!;
    }

    public static IReadOnlyList<LodestoneMatch> Search(IDocument doc)
    {
        var matches = new List<LodestoneMatch>();

        foreach (var link in doc.QuerySelectorAll("a.entry__link"))
        {
            var id = IdFromHref(link.GetAttribute("href"));
            var name = Text(link.QuerySelector(".entry__name"));

            if (id is null || name is null)
            {
                continue;
            }

            // The world sits after an <i> tooltip inside the same paragraph, so
            // it is the element's text, not a child's.
            matches.Add(new LodestoneMatch(id.Value, name, Text(link.QuerySelector(".entry__world"))));
        }

        return matches;
    }

    /// <summary>
    /// Pulls the numeric character id out of a Lodestone path. Also the check
    /// that a search result is a character rather than a free company.
    /// </summary>
    public static long? IdFromHref(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        const string marker = "/character/";
        var at = href.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var rest = href[(at + marker.Length)..];
        var digits = new string(rest.TakeWhile(char.IsAsciiDigit).ToArray());

        return long.TryParse(digits, out var id) && id > 0 ? id : null;
    }

    /// <summary>
    /// Builds a character from the two pages the Lodestone splits it across.
    ///
    /// Every field is optional on purpose. The Lodestone shows a Free Company
    /// block only for characters in one, a title only for characters with one,
    /// and the pages differ by region, so insisting on any single field would
    /// mean failing entirely over something cosmetic.
    /// </summary>
    public static HeraldCharacter Parse(IDocument character, IDocument? classJob, string url)
    {
        var jobs = classJob is null ? [] : Jobs(classJob);
        var job = PickJob(character, jobs);

        return new HeraldCharacter
        {
            Name = Text(character.QuerySelector(".frame__chara__name")) ?? "",
            Realm = Text(character.QuerySelector(".frame__chara__world")),
            RealmRank = Text(character.QuerySelector(".frame__chara__title")),
            Class = job?.Name,
            // The active job's own level, from the character page. The Class/Job
            // page agrees, but only the character page knows which job is worn.
            Level = ActiveLevel(character) ?? job?.Level,
            Race = Block(character, "Race/Clan/Gender"),
            Guild = FreeCompany(character),
            Url = url,
            // The URL is its own version: the Lodestone appends a cache-buster
            // that changes whenever the character re-renders, which is precisely
            // when we want to fetch it again.
            Portrait = Image(character, ".character__detail__image img") is { } portrait
                ? new HeraldPortrait(portrait)
                : null,
            Stats = Stats(character, jobs),
        };
    }

    /// <summary>
    /// What the Lodestone publishes that the shared fields have no room for.
    ///
    /// The profile column is a stack of labelled blocks and the Class/Job page is
    /// every job this character has levelled. Total levels across all of them is the
    /// FFXIV answer to FFXI's total job levels, which is worth having as a token even
    /// though it is not what the leaderboard ranks on.
    /// </summary>
    private static Dictionary<string, string> Stats(IDocument character, IReadOnlyList<LodestoneJob> jobs)
    {
        var stats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (label, key) in new[]
                 {
                     ("Grand Company", "GrandCompany"),
                     ("City-state", "City"),
                     ("Guardian", "Guardian"),
                     ("Nameday", "Nameday"),
                 })
        {
            if (Block(character, label) is { Length: > 0 } value)
            {
                stats[key] = value;
            }
        }

        var levelled = jobs.Where(j => j.Level > 0).ToList();

        if (levelled.Count > 0)
        {
            stats["JobLevels"] = levelled
                .Sum(j => j.Level ?? 0)
                .ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

            stats["JobsLevelled"] = levelled.Count
                .ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (levelled.Count(j => j.Level >= 100) is var capped && capped > 0)
            {
                stats["JobsAtCap"] = capped.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return stats;
    }

    /// <summary>
    /// Reads one of the labelled blocks in the profile column by its label, so a
    /// block being added or reordered above it changes nothing.
    ///
    /// The value is the element straight after the label, not the first value in the
    /// surrounding box. That distinction is a bug this had until the Nameday block
    /// needed reading: Nameday and Guardian share one box, so asking the box for its
    /// first value gave Nameday the Guardian's deity. It also matters because the two
    /// use different classes, __birth and __name, and the sibling rule needs to know
    /// neither.
    /// </summary>
    public static string? Block(IDocument doc, string label)
    {
        foreach (var title in doc.QuerySelectorAll(".character-block__title"))
        {
            if (!string.Equals(Text(title), label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = title.NextElementSibling;

            // A label with nothing after it, or with another label after it, has no
            // value of its own.
            if (name is null || name.ClassList.Contains("character-block__title"))
            {
                continue;
            }

            // "Miqo'te<br />Seeker of the Sun / ♀" is two lines in one element, and
            // TextContent would run them together as "Miqo'teSeeker". The break has
            // to become a separator before the tags come off.
            var html = name.InnerHtml
                .Replace("<br>", " / ", StringComparison.OrdinalIgnoreCase)
                .Replace("<br/>", " / ", StringComparison.OrdinalIgnoreCase)
                .Replace("<br />", " / ", StringComparison.OrdinalIgnoreCase);

            return Squash(StripTags(html));
        }

        return null;
    }

    private static string? FreeCompany(IDocument doc) =>
        Text(doc.QuerySelector(".character__freecompany__name h4"))
        ?? Text(doc.QuerySelector(".character__freecompany__name a"));

    /// <summary>
    /// Absolute and validated, because it ends up in a src the browser fetches.
    /// A relative or javascript: value is dropped rather than repaired.
    /// </summary>
    private static string? Image(IDocument doc, string selector)
    {
        var src = doc.QuerySelector(selector)?.GetAttribute("src");
        return Data.ExternalUrl.TryParse(src, out var safe) ? safe : null;
    }

    private static string StripTags(string html)
    {
        var sb = new System.Text.StringBuilder(html.Length);
        var inside = false;

        foreach (var ch in html)
        {
            if (ch == '<') inside = true;
            else if (ch == '>') inside = false;
            else if (!inside) sb.Append(ch);
        }

        return System.Net.WebUtility.HtmlDecode(sb.ToString());
    }

    private static int? FirstNumber(string text)
    {
        var digits = new string(text.SkipWhile(c => !char.IsAsciiDigit(c))
                                   .TakeWhile(char.IsAsciiDigit).ToArray());

        return int.TryParse(digits, out var n) ? n : null;
    }

    private static string? Text(IElement? element) => Squash(element?.TextContent);

    private static string? Squash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var joined = string.Join(' ', parts);

        return joined.Length == 0 ? null : joined;
    }
}

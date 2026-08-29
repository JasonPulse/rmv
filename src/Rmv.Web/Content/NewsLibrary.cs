using System.Collections.Concurrent;
using System.Globalization;
using Markdig;

namespace Rmv.Web.Content;

/// <summary>One post, front matter parsed and body rendered.</summary>
/// <param name="Slug">From the filename, and the whole of the URL.</param>
/// <param name="Html">Rendered markdown with raw HTML disabled. See NewsLibrary.</param>
public sealed record NewsPost(
    string Slug,
    string Title,
    DateOnly Date,
    string? Author,
    string Html,
    string Excerpt);

/// <summary>
/// Reads markdown posts out of content/news, as content/README.md has specified
/// since the site was scaffolded and nothing implemented.
///
/// A post is a file, so posting is a file copy rather than a rebuild. The
/// directory is mounted read only in both compose and the deployment, and a copy
/// also ships in the image so the section works with no mount at all.
///
/// Filename is YYYY-MM-DD-slug.md and the slug half is the URL. The date comes
/// from front matter rather than the filename, deliberately: a typo in a filename
/// then cannot silently reorder the listing, and a post can be renamed without
/// moving.
///
/// Nothing here throws. A missing directory, an unreadable file or a post with no
/// front matter is a post that does not appear, not a page that 500s.
/// </summary>
public sealed class NewsLibrary(IWebHostEnvironment env, ILogger<NewsLibrary> log)
{
    /// <summary>
    /// Raw HTML is disabled in the pipeline, so a post cannot inject a script tag
    /// or an iframe even though only an operator can write one. The files arrive by
    /// volume mount, which is one misconfigured mount away from being writable by
    /// something else, and the cost of ruling it out is one call.
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAutoLinks()
        .UsePipeTables()
        .Build();

    /// <summary>
    /// Rendered posts, keyed by path, invalidated on the file's write time.
    ///
    /// "Read at request time" is the requirement, and it is met: a changed file is
    /// picked up on the next request with no restart. Re-rendering every post's
    /// markdown on every request would not make that any truer.
    /// </summary>
    private readonly ConcurrentDictionary<string, (DateTime Written, NewsPost Post)> _cache = new();

    private string Directory => Path.Combine(env.ContentRootPath, "content", "news");

    /// <summary>Newest first. Empty when there is no directory or nothing parses.</summary>
    public IReadOnlyList<NewsPost> All()
    {
        string[] files;

        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return [];
            }

            files = System.IO.Directory.GetFiles(Directory, "*.md");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not list news in {Directory}.", Directory);
            return [];
        }

        var posts = new List<NewsPost>(files.Length);

        foreach (var file in files)
        {
            if (Read(file) is { } post)
            {
                posts.Add(post);
            }
        }

        return posts
            .OrderByDescending(p => p.Date)
            .ThenByDescending(p => p.Slug, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// One post by slug, or null.
    ///
    /// Looked up in the listing rather than by building a path from the slug. A
    /// request never reaches the filesystem, so "../../etc/passwd" is simply a slug
    /// that does not match anything.
    /// </summary>
    public NewsPost? Find(string? slug) =>
        string.IsNullOrWhiteSpace(slug)
            ? null
            : All().FirstOrDefault(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase));

    private NewsPost? Read(string path)
    {
        try
        {
            var written = File.GetLastWriteTimeUtc(path);

            if (_cache.TryGetValue(path, out var cached) && cached.Written == written)
            {
                return cached.Post;
            }

            var post = Parse(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path));
            if (post is null)
            {
                return null;
            }

            _cache[path] = (written, post);
            return post;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not read news post {Path}.", path);
            return null;
        }
    }

    /// <summary>
    /// Splits front matter from body and renders the body.
    ///
    /// The front matter is three keys, so it is read line by line rather than by
    /// taking on a YAML parser to do it. A file with no front matter is skipped: it
    /// has no title and no date, and guessing either produces a post nobody meant
    /// to publish.
    /// </summary>
    public static NewsPost? Parse(string fileName, string text)
    {
        var slug = SlugFrom(fileName);
        if (slug is null)
        {
            return null;
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return null;
        }

        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var end = -1;

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                end = i;
                break;
            }

            var colon = lines[i].IndexOf(':');
            if (colon > 0)
            {
                meta[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }
        }

        if (end < 0
            || !meta.TryGetValue("title", out var title)
            || string.IsNullOrWhiteSpace(title)
            || !meta.TryGetValue("date", out var dateText)
            || !DateOnly.TryParse(dateText, CultureInfo.InvariantCulture, out var date))
        {
            return null;
        }

        var body = string.Join('\n', lines.Skip(end + 1)).Trim();

        return new NewsPost(
            slug,
            title.Trim(),
            date,
            meta.TryGetValue("author", out var author) && author.Length > 0 ? author : null,
            Markdown.ToHtml(body, Pipeline),
            Excerpt(body));
    }

    /// <summary>
    /// The slug half of YYYY-MM-DD-slug. Refused rather than repaired if the shape
    /// is wrong, because the slug is a URL and this is the only place it is decided.
    /// </summary>
    public static string? SlugFrom(string fileName)
    {
        // 11 characters of date and separator, then at least one of slug.
        if (fileName.Length < 12 || fileName[10] != '-')
        {
            return null;
        }

        if (!DateOnly.TryParseExact(fileName[..10], "yyyy-MM-dd", out _))
        {
            return null;
        }

        var slug = fileName[11..];

        return slug.Length > 0 && slug.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? slug.ToLowerInvariant()
            : null;
    }

    /// <summary>
    /// The first paragraph as plain text, for the listing. Markdown syntax is
    /// stripped by rendering and then taking the text, rather than by a second set
    /// of rules that would disagree with the renderer.
    /// </summary>
    private static string Excerpt(string body)
    {
        var paragraph = body.Split("\n\n", 2)[0].Trim();
        var text = Markdown.ToPlainText(paragraph, Pipeline).Replace('\n', ' ').Trim();

        return text.Length <= 220 ? text : text[..220].TrimEnd() + "...";
    }
}

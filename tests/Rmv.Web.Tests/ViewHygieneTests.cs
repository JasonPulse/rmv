using System.Text.RegularExpressions;

namespace Rmv.Web.Tests;

/// <summary>
/// Rules about the markup itself, checked against the source.
///
/// Unusual for this suite, and worth it for one property: no view may carry an
/// inline event handler or an inline script. That is what let a member-supplied
/// name reach a JavaScript string, which Razor cannot make safe, and the content
/// policy now refuses inline script outright. Without this the failure is quiet:
/// the browser blocks the handler, the confirm dialog simply never appears, and a
/// delete goes through with no prompt.
///
/// Reading the source is the only way to assert it. A rendered page would only
/// cover the pages a test happens to render, and the point is that none of them do
/// this.
/// </summary>
public class ViewHygieneTests
{
    /// <summary>
    /// The views, found by walking up from the test assembly to the repository.
    ///
    /// Fails loudly if the layout ever changes, rather than passing on an empty
    /// list, which is the way a test like this rots.
    /// </summary>
    private static IReadOnlyList<string> Views()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Rmv.Web")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        var views = Directory
            .GetFiles(Path.Combine(dir.FullName, "src", "Rmv.Web", "Pages"), "*.cshtml",
                SearchOption.AllDirectories)
            .ToList();

        Assert.NotEmpty(views);

        return views;
    }

    private static string Name(string path) =>
        path[(path.IndexOf("Pages", StringComparison.Ordinal))..];

    [Fact]
    public void No_view_carries_an_inline_event_handler()
    {
        // onsubmit, onclick, onerror and the rest. Each one is a place where Razor
        // encodes for HTML and the browser then hands the result to a JavaScript
        // parser, which is how "');alert(1)//" as an alias ran in an admin's
        // browser. The site uses data-confirm and one listener instead.
        var handler = new Regex(
            @"\son(?:submit|click|change|input|error|load|focus|blur|mouseover|keydown|keyup)\s*=",
            RegexOptions.IgnoreCase);

        var offenders = Views()
            .Where(v => handler.IsMatch(File.ReadAllText(v)))
            .Select(Name)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_view_carries_an_inline_script_block()
    {
        // script-src is 'self' with no unsafe-inline, so an inline block would be
        // blocked at runtime and nothing would say so. Scripts live in wwwroot/js.
        var inline = new Regex(@"<script(?![^>]*\ssrc\s*=)[^>]*>", RegexOptions.IgnoreCase);

        var offenders = Views()
            .Where(v => inline.IsMatch(File.ReadAllText(v)))
            .Select(Name)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_view_carries_a_javascript_url()
    {
        var offenders = Views()
            .Where(v => File.ReadAllText(v).Contains("javascript:", StringComparison.OrdinalIgnoreCase))
            .Select(Name)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_confirm_prompt_is_somewhere_that_submits()
    {
        // The real failure mode, and one I walked into building the signature
        // editor: confirm.js listens for a submit and reads the message off the
        // submitter or the form. A data-confirm on anything else is a prompt that
        // never appears, and a delete that goes through without asking.
        //
        // This replaced an assertion listing the files that had one, which failed
        // every time a legitimate new prompt was added and told nobody anything.
        var attribute = new Regex(@"data-confirm\s*=", RegexOptions.IgnoreCase);

        var offenders = new List<string>();

        foreach (var view in Views())
        {
            var markup = File.ReadAllText(view);

            foreach (var match in attribute.Matches(markup).Cast<Match>())
            {
                // The tag this attribute is inside.
                var open = markup.LastIndexOf('<', match.Index);
                if (open < 0)
                {
                    offenders.Add($"{Name(view)}: not inside a tag");
                    continue;
                }

                var close = markup.IndexOf('>', match.Index);
                var tag = markup[open..(close < 0 ? markup.Length : close)];

                var submits = tag.StartsWith("<form", StringComparison.OrdinalIgnoreCase)
                              || (tag.StartsWith("<button", StringComparison.OrdinalIgnoreCase)
                                  && tag.Contains("type=\"submit\"", StringComparison.OrdinalIgnoreCase));

                if (!submits)
                {
                    offenders.Add($"{Name(view)}: {tag[..Math.Min(60, tag.Length)]}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_prompts_have_not_all_quietly_disappeared()
    {
        // The other half: the check above passes trivially if somebody deletes every
        // prompt. Six existed when the inline handlers were removed and the editor
        // added two more.
        var count = Views()
            .Sum(v => Regex.Matches(File.ReadAllText(v), "data-confirm").Count);

        Assert.True(count >= 6, $"only {count} confirm prompts left");
    }
}

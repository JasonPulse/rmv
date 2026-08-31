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
    public void The_confirm_prompts_are_data_attributes()
    {
        // Six of them, and this is the shape they have to keep. Named so a seventh
        // written the old way is caught here rather than by nothing.
        var withConfirm = Views()
            .Where(v => File.ReadAllText(v).Contains("data-confirm", StringComparison.Ordinal))
            .Select(Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                "Pages/Admin/History.cshtml",
                "Pages/Admin/Members.cshtml",
                "Pages/Characters/Index.cshtml",
                "Pages/Tools/Daoc/Spellcraft.cshtml",
            ],
            withConfirm.Select(n => n.Replace('\\', '/')).ToList());
    }
}

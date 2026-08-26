using Rmv.Web.Data;

namespace Rmv.Web.Tests;

public class ExternalUrlTests
{
    [Theory]
    [InlineData("https://herald.uthgard.net/herald.php?view=overview")]
    [InlineData("http://example.org")]
    [InlineData("https://example.org/a/b?c=d&e=f#g")]
    [InlineData("  https://example.org/padded  ")]
    public void Accepts_ordinary_http_and_https(string input)
    {
        Assert.True(ExternalUrl.TryParse(input, out var normalised));
        Assert.StartsWith("http", normalised);
    }

    [Theory]
    // The reason the scheme is an allowlist. Escaping does nothing about these:
    // href="javascript:alert(1)" is well-formed HTML and still executes.
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("  javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///etc/passwd")]
    // Not absolute, so it cannot be verified as leaving the site safely.
    [InlineData("/relative/path")]
    [InlineData("example.org")]
    [InlineData("//example.org")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_anything_that_is_not_an_http_url(string? input)
    {
        Assert.False(ExternalUrl.TryParse(input, out var normalised));
        Assert.Null(normalised);
    }

    [Fact]
    public void Rejects_a_url_past_the_length_cap()
    {
        var tooLong = "https://example.org/" + new string('a', ExternalUrl.MaxLength);

        Assert.False(ExternalUrl.IsValid(tooLong));
    }

    [Fact]
    public void Normalises_so_what_is_stored_is_what_was_parsed()
    {
        Assert.True(ExternalUrl.TryParse(" HTTPS://Example.ORG/Path ", out var normalised));
        // Host lowercased by Uri, path case preserved.
        Assert.Equal("https://example.org/Path", normalised);
    }

    [Fact]
    public void Host_is_exposed_for_the_title_attribute()
    {
        var link = new GameLink { Url = "https://herald.uthgard.net/herald.php" };

        Assert.Equal("herald.uthgard.net", link.Host);
    }

    [Fact]
    public void Label_falls_back_to_the_kind_when_blank()
    {
        Assert.Equal("Herald", new GameLink { Kind = GameLinkKind.Herald, Label = "  " }.DisplayLabel);
        Assert.Equal("Uthgard Herald", new GameLink { Kind = GameLinkKind.Herald, Label = "Uthgard Herald" }.DisplayLabel);
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Rmv.Web.Data;
using Rmv.Web.Security;

namespace Rmv.Web.Tests;

/// <summary>
/// What every response tells a browser it may do.
///
/// This exists because a scanner asked for /admin/.aws/credentials.bak. It found
/// nothing, as every probe against this site does, and "no file leaks" is only
/// half of not exposing anything: the other half is that a browser cannot be
/// talked into running something on our behalf.
///
/// The policy is asserted by directive rather than as one long string, so a test
/// failure names the thing that changed.
/// </summary>
public class SecurityHeaderTests
{
    private static async Task<IHeaderDictionary> HeadersAsync(
        params (string Key, string? Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s =>
                new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var context = new DefaultHttpContext();
        var middleware = new SecurityHeaders(_ => Task.CompletedTask, config);

        await middleware.InvokeAsync(context);

        return context.Response.Headers;
    }

    private static async Task<Dictionary<string, string>> PolicyAsync(
        params (string Key, string? Value)[] settings)
    {
        var headers = await HeadersAsync(settings);

        return headers["Content-Security-Policy"].ToString()
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Split(' ', 2))
            .ToDictionary(d => d[0], d => d.Length > 1 ? d[1] : "");
    }

    [Fact]
    public async Task Inline_script_is_refused()
    {
        // The point of the whole exercise. Six forms used to interpolate a
        // member-supplied name into an onsubmit attribute, which Razor cannot make
        // safe, and this is what makes that impossible to reintroduce quietly.
        var policy = await PolicyAsync();

        Assert.Equal("'self'", policy["script-src"]);
        Assert.DoesNotContain("unsafe-inline", policy["script-src"]);
        Assert.DoesNotContain("unsafe-eval", policy["script-src"]);
    }

    [Fact]
    public async Task Only_discord_may_serve_an_image_from_somewhere_else()
    {
        // The only third party the site loads anything from, and only an avatar for
        // a signed-in member.
        var policy = await PolicyAsync();

        Assert.Equal("'self' https://cdn.discordapp.com", policy["img-src"]);
    }

    [Fact]
    public async Task The_page_cannot_be_framed_or_have_its_base_moved()
    {
        var policy = await PolicyAsync();
        var headers = await HeadersAsync();

        Assert.Equal("'none'", policy["frame-ancestors"]);
        Assert.Equal("'self'", policy["base-uri"]);
        Assert.Equal("'self'", policy["form-action"]);
        Assert.Equal("'none'", policy["object-src"]);
        Assert.Equal("DENY", headers["X-Frame-Options"]);
    }

    [Fact]
    public async Task Style_attributes_are_allowed_and_only_style_attributes()
    {
        // A handful of views set a margin or a custom property inline, and an
        // attribute cannot carry a hash. None of them interpolate anything a member
        // typed; the only computed one is a bar height, which is a number.
        var policy = await PolicyAsync();

        Assert.Contains("'unsafe-inline'", policy["style-src"]);
        Assert.DoesNotContain("unsafe-inline", policy["script-src"]);
    }

    [Fact]
    public async Task Nothing_is_sniffed_and_nothing_is_permitted()
    {
        var headers = await HeadersAsync();

        // The one that matters for the image endpoints: they echo a content type
        // read from the file's own bytes.
        Assert.Equal("nosniff", headers["X-Content-Type-Options"]);

        Assert.Equal("strict-origin-when-cross-origin", headers["Referrer-Policy"]);

        var permissions = headers["Permissions-Policy"].ToString();
        foreach (var feature in new[] { "camera", "geolocation", "microphone", "payment", "usb" })
        {
            Assert.Contains($"{feature}=()", permissions);
        }
    }

    [Fact]
    public async Task The_analytics_origin_is_allowed_only_when_it_is_configured()
    {
        // Off by default, and the page renders no script tag either. One
        // configuration, one decision; see _Layout.
        var off = await PolicyAsync();
        Assert.Equal("'self'", off["script-src"]);
        Assert.Equal("'self'", off["connect-src"]);

        var on = await PolicyAsync(
            ("Analytics:UmamiScriptUrl", "https://stats.example.com/script.js"),
            ("Analytics:UmamiWebsiteId", "abc"));

        // The origin, not the URL: a policy source is an origin, and the path would
        // simply be ignored.
        Assert.Equal("'self' https://stats.example.com", on["script-src"]);
        Assert.Equal("'self' https://stats.example.com", on["connect-src"]);
    }

    [Theory]
    // A misconfigured value must not widen the policy. Anything that is not an
    // absolute http or https URL is no origin at all.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/local/script.js")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/javascript,alert(1)")]
    [InlineData("*")]
    public async Task A_bad_analytics_url_allows_nothing(string url)
    {
        var policy = await PolicyAsync(("Analytics:UmamiScriptUrl", url));

        Assert.Equal("'self'", policy["script-src"]);
    }

    [Fact]
    public async Task Everything_else_falls_back_to_this_site()
    {
        var policy = await PolicyAsync();

        Assert.Equal("'self'", policy["default-src"]);
        Assert.Equal("'self'", policy["font-src"]);
    }

    // --- pictures and crawlers ------------------------------------------------

    [Fact]
    public void A_picture_says_it_does_not_belong_in_an_image_search()
    {
        var http = new DefaultHttpContext();

        StoredImage.Bytes(http, [1, 2, 3], "image/png", StoredImage.ETagFor("v1"));

        // robots.txt stops the fetch for a crawler that reads it. This is the half
        // that keeps a screenshot out of Google Images when it found the URL
        // somewhere else.
        Assert.Equal("noindex", http.Response.Headers["X-Robots-Tag"]);
    }

    [Fact]
    public void Nothing_is_sent_for_a_picture_that_is_not_there()
    {
        var http = new DefaultHttpContext();

        StoredImage.Bytes(http, null, "image/png", StoredImage.ETagFor("v1"));

        Assert.False(http.Response.Headers.ContainsKey("X-Robots-Tag"));
    }

    [Fact]
    public void Robots_keeps_crawlers_off_every_route_that_serves_a_picture()
    {
        var robots = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Rmv.Web", "wwwroot", "robots.txt"));

        // The three picture routes. /characters is already disallowed as a prefix,
        // which is what covers /characters/1/portrait.
        Assert.Contains("Disallow: /gallery/*/image", robots);
        Assert.Contains("Disallow: /sig/", robots);
        Assert.Contains("Disallow: /characters", robots);

        // And the 2001 generator's paths, which Googlebot-Image was retrying every
        // couple of days against a file that has not existed since that site did.
        Assert.Contains("Disallow: /daoc/", robots);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ResultsMayVary.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        return dir.FullName;
    }
}

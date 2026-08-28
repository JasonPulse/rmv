using Rmv.Web.Analytics;

namespace Rmv.Web.Tests;

/// <summary>
/// The domain a request was referred from.
///
/// This exists for a specific question. Requests for a DAoC signature generator
/// that has been gone for ten years are still arriving, for characters that belong
/// to us, and the useful thing to know is which site is still embedding them. A
/// browser loading an img in someone else's page sends that page's URL as Referer,
/// and the host of it is the answer.
/// </summary>
public class ReferrerHostTests
{
    [Theory]
    [InlineData("https://forum.example.com/thread/1234", "forum.example.com")]
    [InlineData("http://vnboards.ign.com/topic/99", "vnboards.ign.com")]
    // Case and port normalised away, so one site is one row.
    [InlineData("https://Forum.Example.COM/x", "forum.example.com")]
    [InlineData("https://forum.example.com:8443/x", "forum.example.com")]
    // No path at all is still a host.
    [InlineData("https://example.com", "example.com")]
    [InlineData("https://example.com/", "example.com")]
    // Query and fragment are not part of it.
    [InlineData("https://example.com/p?a=b#c", "example.com")]
    public void Reads_the_host_out_of_a_referrer(string referrer, string expected)
    {
        Assert.Equal(expected, RequestLogMiddleware.HostOf(referrer));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // A relative Referer is not something a browser sends.
    [InlineData("/history")]
    [InlineData("not a url")]
    // Only http and https are a site referring to us. Anything else is a client
    // making something up, and it is not a domain worth recording.
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/x")]
    public void Anything_that_is_not_a_site_is_no_host_at_all(string? referrer)
    {
        Assert.Null(RequestLogMiddleware.HostOf(referrer));
    }

    [Fact]
    public void An_absurdly_long_host_is_no_host_at_all()
    {
        // The header comes from the caller, so its length is not ours to trust.
        // Uri rejects a label over 63 characters, so this is null rather than a
        // shortened version of something that was never a domain.
        var host = new string('a', 300) + ".example.com";

        Assert.Null(RequestLogMiddleware.HostOf($"https://{host}/x"));
    }

    [Fact]
    public void A_host_at_the_dns_limit_still_reads()
    {
        // Every label under the 63 character DNS limit and 249 in total, so it fits
        // the column and is stored whole rather than cut.
        var host = string.Join('.', Enumerable.Repeat(new string('a', 62), 3))
                   + "." + new string('b', 60);

        Assert.True(host.Length <= RequestLogMiddleware.MaxHostLength, $"{host.Length} chars");
        Assert.Equal(host, RequestLogMiddleware.HostOf($"https://{host}/x"));
    }

    [Fact]
    public void An_ip_address_is_a_host_like_any_other()
    {
        Assert.Equal("192.0.2.10", RequestLogMiddleware.HostOf("http://192.0.2.10/page"));
    }

    /// <summary>The migration's expression, so the two can be compared directly.</summary>
    private const string BackfillPattern =
        "^https?://([^/?#:]{1,253})(?::[0-9]{1,5})?(?:[/?#]|$)";

    private static string? Backfilled(string referrer)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            referrer, BackfillPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    [Theory]
    // The shapes the migration's SQL backfill has to agree with, since it fills in
    // rows that were logged before this column existed.
    [InlineData("https://forum.example.com/thread/1234")]
    [InlineData("http://a.b.c.example.co.uk/x?y=1")]
    [InlineData("https://example.com")]
    [InlineData("https://example.com/")]
    [InlineData("https://Forum.Example.COM/x")]
    [InlineData("https://a.b.example.co.uk/x?y=1#z")]
    // The one that caught a real disagreement. Uri.Host drops the port, and the
    // first version of the backfill kept it, so one forum would have shown up as
    // both "vnboards.ign.com" and "vnboards.ign.com:8443".
    [InlineData("https://vnboards.ign.com:8443/topic/999")]
    [InlineData("http://example.com:80/x")]
    // And the shapes where both must answer nothing.
    [InlineData("javascript:alert(1)")]
    [InlineData("/history")]
    [InlineData("ftp://example.com/x")]
    [InlineData("not a url")]
    public void The_backfill_and_the_parser_agree(string referrer)
    {
        Assert.Equal(RequestLogMiddleware.HostOf(referrer), Backfilled(referrer));
    }

    [Fact]
    public void The_backfill_and_the_parser_agree_that_an_absurd_host_is_nothing()
    {
        // An unanchored pattern would have stored the first 253 characters of a 300
        // character host, so a row logged before the column existed would carry a
        // "domain" that live logging would never produce.
        var referrer = "https://" + new string('a', 300) + ".example.com/x";

        Assert.Null(Backfilled(referrer));
        Assert.Null(RequestLogMiddleware.HostOf(referrer));
    }

    [Fact]
    public void A_host_at_the_dns_limit_reads_the_same_both_ways()
    {
        var host = string.Join('.', Enumerable.Repeat(new string('a', 62), 3))
                   + "." + new string('b', 60);
        var referrer = $"https://{host}/x";

        Assert.Equal(host, RequestLogMiddleware.HostOf(referrer));
        Assert.Equal(host, Backfilled(referrer));
    }
}

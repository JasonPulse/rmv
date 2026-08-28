namespace Rmv.Web.Data;

/// <summary>
/// One HTTP request. This is the server-side half of analytics: it sees every
/// request including 404s, redirects, and scanner probes, none of which a
/// JavaScript beacon can observe.
///
/// Deliberately holds no IP address and no cookie. Country comes from
/// Cloudflare's CF-IPCountry header, which is coarse enough not to identify
/// anyone, so there is nothing here that needs a consent banner.
/// </summary>
public class RequestLog
{
    public long Id { get; set; }

    public DateTimeOffset At { get; set; }

    public string Method { get; set; } = "";

    /// <summary>Path and query, truncated. Query is kept because "what were they trying" is the point.</summary>
    public string Path { get; set; } = "";

    public int Status { get; set; }

    /// <summary>Round-trip in milliseconds, for spotting slow pages.</summary>
    public int DurationMs { get; set; }

    public string? Referrer { get; set; }

    /// <summary>
    /// Just the host from Referrer, lowercased, or null when there is no referrer.
    ///
    /// Stored rather than derived at read time so grouping is an indexed GROUP BY
    /// instead of parsing four hundred characters of URL per row in memory. The
    /// question it answers is "which site is sending these", and for a request that
    /// is an img embedded in someone else's page, the host is the whole answer.
    /// </summary>
    public string? ReferrerHost { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>ISO-3166 alpha-2 from CF-IPCountry, or null off Cloudflare.</summary>
    public string? Country { get; set; }

    /// <summary>Set when the user agent is a known crawler, so humans can be filtered out.</summary>
    public bool IsBot { get; set; }
}

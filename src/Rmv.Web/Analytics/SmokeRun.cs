using Rmv.Web.Data;

namespace Rmv.Web.Analytics;

/// <summary>
/// Our own smoke run, and the one question about it: which rows in the log are us.
///
/// tools/smoke.sh drives every route on the site, including the ones it expects to
/// miss: /no-such-page, /roster/999999, /news/no-such-post, /gallery/999999/image,
/// /sig/short.png and /admin/.aws/credentials.bak. Those are assertions, not
/// traffic. Run a few times a day against production they were a third of the log
/// and a fifth of the 404 panel, which is the panel that exists to show what
/// scanners are actually asking for.
///
/// Still recorded, not skipped. A request that a client-supplied header can keep out
/// of the log is a blind spot somebody can walk through by setting one header, and
/// the point of the server-side log is that it sees everything. The rows are marked
/// as ours and every panel leaves them out.
/// </summary>
public static class SmokeRun
{
    /// <summary>
    /// What smoke.sh sends. The version is there so a later run can be told from an
    /// older one, and the URL so anybody reading a log elsewhere can tell what it is.
    ///
    /// tools/smoke.sh has to send this exact string, and nothing but a test can hold
    /// a bash script and a C# constant together; SmokeRunTests does.
    /// </summary>
    public const string UserAgent = "rmv-smoke/1 (+https://github.com/JasonPulse/rmv)";

    /// <summary>
    /// Matched on the prefix, so the version can move without the analytics page
    /// losing sight of what it means.
    /// </summary>
    public const string Prefix = "rmv-smoke/";

    /// <summary>In memory, for the middleware.</summary>
    public static bool Sent(string? userAgent) =>
        userAgent is not null && userAgent.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// On a query, for the analytics panels. Every one of them, including the two
    /// that deliberately include bots, because our own deliberate 404s are exactly
    /// what those two are for and exactly what they were burying.
    /// </summary>
    public static IQueryable<RequestLog> NotOurs(this IQueryable<RequestLog> logs)
    {
        ArgumentNullException.ThrowIfNull(logs);

        return logs.Where(r => r.UserAgent == null || !r.UserAgent.StartsWith(Prefix));
    }
}

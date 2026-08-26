using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Rmv.Web.Data;

namespace Rmv.Web.Analytics;

public sealed class RequestLogMiddleware(RequestDelegate next, RequestLogWriter writer)
{
    /// <summary>
    /// Not worth recording. Static assets and health probes would be most of the
    /// table and none of the insight; the health endpoints in particular are hit
    /// every few seconds by kubelet.
    /// </summary>
    private static readonly string[] IgnoredPrefixes =
    [
        "/css/", "/js/", "/fonts/", "/img/", "/healthz/",
    ];

    private static readonly string[] IgnoredPaths =
    [
        "/favicon.ico", "/site.webmanifest",
    ];

    // Substrings that identify a crawler well enough to filter humans from bots.
    private static readonly string[] BotMarkers =
    [
        "bot", "crawler", "spider", "curl", "wget", "python-requests", "httpclient",
        "scanner", "headlesschrome", "facebookexternalhit", "slackbot", "discordbot",
        "monitor", "uptime", "probe",
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        // UseStatusCodePagesWithReExecute runs the pipeline a second time to
        // render /Error, which reaches this middleware again. Logging that would
        // record every 404 twice: once as the path the visitor asked for and once
        // as /Error?code=404. Only the first is the truth.
        if (context.Features.Get<IStatusCodeReExecuteFeature>() is not null)
        {
            await next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "/";

        if (ShouldIgnore(path))
        {
            await next(context);
            return;
        }

        var started = Stopwatch.GetTimestamp();

        try
        {
            await next(context);
        }
        finally
        {
            var ua = context.Request.Headers.UserAgent.ToString();

            writer.Enqueue(new RequestLog
            {
                At = DateTimeOffset.UtcNow,
                Method = context.Request.Method,
                // Query included: "what were they trying to hit" is the question.
                // Non-null by construction; Truncate's nullable return is for the
                // optional headers below.
                Path = Truncate(path + context.Request.QueryString.Value, 400)!,
                Status = context.Response.StatusCode,
                DurationMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Referrer = Truncate(NullIfEmpty(context.Request.Headers.Referer.ToString()), 400),
                UserAgent = Truncate(NullIfEmpty(ua), 400),
                // Cloudflare adds this at the edge; null when not behind it.
                Country = NullIfEmpty(context.Request.Headers["CF-IPCountry"].ToString()) is { } c
                          && c.Length == 2 ? c.ToUpperInvariant() : null,
                IsBot = LooksLikeBot(ua),
            });
        }
    }

    private static bool ShouldIgnore(string path) =>
        IgnoredPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
        || IgnoredPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeBot(string ua) =>
        string.IsNullOrEmpty(ua)
        || BotMarkers.Any(m => ua.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max];
}

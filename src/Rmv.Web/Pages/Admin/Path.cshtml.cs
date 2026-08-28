using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Analytics;
using Rmv.Web.Data;

namespace Rmv.Web.Pages.Admin;

/// <summary>
/// Everything the log knows about one path.
///
/// The counts on the analytics page answer "what is being hit" and stop there. The
/// question that actually came up was different: requests for a DAoC signature
/// generator that has been gone for ten years are still arriving, for characters
/// that belong to us, and where are they coming from.
///
/// Three signals answer that, and which one answers it depends on the caller. A
/// browser loading an img embedded in a forum post sends a Referer, so the host is
/// the answer. A crawler replaying a URL it indexed years ago sends none, and then
/// the user agent is the answer. Neither is much use without knowing whether the
/// hits are one visitor or a hundred, so the timeline is here too.
/// </summary>
public class PathModel(RmvDbContext db) : PageModel
{
    public string Path { get; private set; } = "";

    public int Days { get; private set; } = 90;

    public int Hits { get; private set; }

    public int BotHits { get; private set; }

    public DateTimeOffset? FirstSeen { get; private set; }

    public DateTimeOffset? LastSeen { get; private set; }

    public IReadOnlyList<Count> ReferrerHosts { get; private set; } = [];

    public IReadOnlyList<Count> UserAgents { get; private set; } = [];

    public IReadOnlyList<Count> Countries { get; private set; } = [];

    public IReadOnlyList<StatusCount> Statuses { get; private set; } = [];

    public IReadOnlyList<Hit> Recent { get; private set; } = [];

    public record Hit(
        DateTimeOffset At, int Status, string? ReferrerHost, string? UserAgent, string? Country, bool IsBot);

    public async Task<IActionResult> OnGetAsync(string? p, int? days, CancellationToken ct)
    {
        // Matched exactly, including the query string, because that is how the log
        // stores it and because "?chars=property" is the interesting part.
        Path = (p ?? "").Trim();

        if (Path.Length is 0 or > 400)
        {
            return RedirectToPage("/Admin/Analytics");
        }

        Days = days is >= 1 and <= 365 ? days.Value : 90;

        var since = DateTimeOffset.UtcNow.AddDays(-Days);
        var rows = db.RequestLogs.Where(r => r.Path == Path && r.At >= since);

        Hits = await rows.CountAsync(ct);

        if (Hits == 0)
        {
            return Page();
        }

        BotHits = await rows.CountAsync(r => r.IsBot, ct);
        FirstSeen = await rows.MinAsync(r => r.At, ct);
        LastSeen = await rows.MaxAsync(r => r.At, ct);

        // "(none)" rather than dropping the row. For a ten year old signature URL,
        // "every one of these arrived with no referrer" is the finding, not an
        // absence of data.
        ReferrerHosts = await RequestLogQueries.TopAsync(rows, r => r.ReferrerHost, 20, ct);
        UserAgents = await RequestLogQueries.TopAsync(rows, r => r.UserAgent, 20, ct);
        Countries = await RequestLogQueries.TopAsync(rows, r => r.Country, 20, ct);

        Statuses = await RequestLogQueries.StatusesAsync(rows, ct);

        Recent = await rows
            .OrderByDescending(r => r.At)
            .Take(40)
            .Select(r => new Hit(r.At, r.Status, r.ReferrerHost, r.UserAgent, r.Country, r.IsBot))
            .ToListAsync(ct);

        return Page();
    }
}

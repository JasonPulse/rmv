using System.Linq.Expressions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Analytics;
using Rmv.Web.Data;

namespace Rmv.Web.Pages.Admin;

public class AnalyticsModel(RmvDbContext db) : PageModel
{
    public int Days { get; private set; } = 7;

    public bool ExcludeBots { get; private set; } = true;

    public int TotalRequests { get; private set; }

    public int HumanRequests { get; private set; }

    public IReadOnlyList<Count> TopPaths { get; private set; } = [];

    /// <summary>The question actually asked: what are people trying to hit that is not there.</summary>
    public IReadOnlyList<Count> TopMisses { get; private set; } = [];

    public IReadOnlyList<Count> TopReferrers { get; private set; } = [];

    /// <summary>
    /// Domains rather than full URLs. Asked for, and the better view: fifty forum
    /// thread URLs from one site are one line here and fifty in TopReferrers.
    /// </summary>
    public IReadOnlyList<Count> TopReferrerHosts { get; private set; } = [];

    public IReadOnlyList<Count> TopCountries { get; private set; } = [];

    public IReadOnlyList<StatusCount> Statuses { get; private set; } = [];

    public IReadOnlyList<DayCount> PerDay { get; private set; } = [];

    public IReadOnlyList<SlowPath> Slowest { get; private set; } = [];

    /// <summary>
    /// An empty panel is usually the bot filter rather than no data. Saying which
    /// stops it reading as a broken page.
    /// </summary>
    public string EmptyReason => TotalRequests > 0 && HumanRequests == 0 && ExcludeBots
        ? "Every request in this window came from a bot. Use \u201cinclude bots\u201d to see them."
        : "Nothing recorded in this window yet.";

    public record DayCount(DateOnly Day, int Total);

    public record SlowPath(string Path, int WorstMs, int Total);

    public async Task OnGetAsync(int? days, bool? bots, CancellationToken ct)
    {
        Days = days is >= 1 and <= 90 ? days.Value : 7;
        ExcludeBots = bots is not true;

        var since = DateTimeOffset.UtcNow.AddDays(-Days);

        var all = db.RequestLogs.Where(r => r.At >= since);
        var scoped = ExcludeBots ? all.Where(r => !r.IsBot) : all;

        TotalRequests = await all.CountAsync(ct);
        HumanRequests = await all.CountAsync(r => !r.IsBot, ct);

        TopPaths = await RequestLogQueries.TopAsync(scoped.Where(r => r.Status < 400), r => r.Path, 25, ct);

        // Bots are included here on purpose: a flood of 404s for /wp-login.php is
        // exactly what this panel is for.
        TopMisses = await RequestLogQueries.TopAsync(all.Where(r => r.Status == 404), r => r.Path, 25, ct);

        TopReferrers = await RequestLogQueries.TopAsync(scoped.Where(r => r.Referrer != null), r => r.Referrer!, 15, ct);

        TopCountries = await RequestLogQueries.TopAsync(scoped.Where(r => r.Country != null), r => r.Country!, 15, ct);

        // Bots included, deliberately. A crawler is a real source of traffic, and
        // excluding them here hid the answer to "who is still requesting this".
        TopReferrerHosts = await RequestLogQueries.TopAsync(
            all.Where(r => r.ReferrerHost != null), r => r.ReferrerHost!, 20, ct);
        var unusedTail = (await scoped
                .Where(r => r.Country != null)
                .GroupBy(r => r.Country!)
                .Select(g => new { Key = g.Key, Total = g.Count() })
                .OrderByDescending(x => x.Total).Take(15)
                .ToListAsync(ct))
            .Select(x => new Count(x.Key, x.Total)).ToList();

        Statuses = await RequestLogQueries.StatusesAsync(scoped, ct);

        PerDay = (await scoped
                .GroupBy(r => new { r.At.Year, r.At.Month, r.At.Day })
                .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, Total = g.Count() })
                .ToListAsync(ct))
            .Select(d => new DayCount(new DateOnly(d.Year, d.Month, d.Day), d.Total))
            .OrderBy(d => d.Day)
            .ToList();

        // Max rather than a true percentile: Postgres has percentile_cont but EF
        // cannot express it, and one raw query here is not worth the maintenance.
        // The slowest single hit is the useful signal anyway.
        Slowest = (await scoped
                .GroupBy(r => r.Path)
                .Where(g => g.Count() >= 3)
                .Select(g => new { Key = g.Key, Worst = g.Max(r => r.DurationMs), Total = g.Count() })
                .OrderByDescending(x => x.Worst).Take(12)
                .ToListAsync(ct))
            .Select(x => new SlowPath(x.Key, x.Worst, x.Total)).ToList();
    }
}

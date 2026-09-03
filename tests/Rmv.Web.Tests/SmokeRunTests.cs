using Microsoft.EntityFrameworkCore;
using Rmv.Web.Analytics;
using Rmv.Web.Data;
using Rmv.Web.Pages.Admin;

namespace Rmv.Web.Tests;

/// <summary>
/// The one thing holding a bash script and a C# constant together.
///
/// tools/smoke.sh names itself in a header so the analytics panels can leave its
/// requests out. Nothing at compile time can check that a shell string still matches
/// the constant the site filters on, and getting it wrong is silent: the script keeps
/// passing, and its deliberate 404s quietly reappear in the panel that exists to show
/// what scanners are asking for.
/// </summary>
public class SmokeRunTests
{
    private static string Script() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "smoke.sh"));

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

    [Fact]
    public void The_script_sends_the_agent_the_site_filters_on()
    {
        var script = Script();

        Assert.Contains($"AGENT=\"{SmokeRun.UserAgent}\"", script);
        Assert.True(SmokeRun.Sent(SmokeRun.UserAgent));
    }

    [Fact]
    public void Every_request_the_script_makes_carries_it()
    {
        // A bare curl added later would be an untagged request, which is how this
        // drifts back: one new check, invisible in the panel, nobody notices.
        var lines = Script()
            .Split('\n')
            .Where(l => l.Contains("curl ") && !l.TrimStart().StartsWith('#'))
            .ToList();

        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.Contains("-A \"$AGENT\"", line));
    }

    [Theory]
    [InlineData("rmv-smoke/1 (+https://github.com/JasonPulse/rmv)", true)]
    [InlineData("rmv-smoke/2", true)]
    [InlineData("RMV-SMOKE/1", true)]
    [InlineData("curl/8.7.1", false)]
    [InlineData("Mozilla/5.0 (Macintosh)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Ours_is_matched_on_the_prefix(string? userAgent, bool ours)
    {
        // The version can move without the panels losing sight of what it means, and
        // somebody else's agent is never ours.
        Assert.Equal(ours, SmokeRun.Sent(userAgent));
    }
}

/// <summary>The panels, against a database, with a smoke row in the table.</summary>
[Trait("Category", "Database")]
[Collection(NetworkCollection.Name)]
public class AnalyticsPanelTests : HeraldDatabaseTests
{
    protected override void ConfigureHerald(FakeHeraldAdapter herald) { }

    /// <summary>
    /// The log is not scoped to a member, so the base class's cleanup cannot know
    /// about it. Cleared per test rather than per class, because these count rows.
    /// </summary>
    protected override async Task SeedAsync() =>
        await Db.RequestLogs.ExecuteDeleteAsync();

    private async Task LogAsync(string? userAgent, string path, int status)
    {
        Db.RequestLogs.Add(new RequestLog
        {
            At = DateTimeOffset.UtcNow.AddMinutes(-1),
            Method = "GET",
            Path = path,
            Status = status,
            DurationMs = 3,
            UserAgent = userAgent,
            IsBot = userAgent is null || SmokeRun.Sent(userAgent) || userAgent.Contains("bot"),
        });

        await Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Our_own_run_is_counted_apart_and_kept_out_of_every_panel()
    {
        // What he was looking at: the misses panel deliberately includes bots,
        // because a flood of /wp-login.php is the whole point of it. Our own run
        // asserts that /roster/999999 still 404s, several times a day, and those
        // were sitting in the same list.
        await LogAsync(SmokeRun.UserAgent, "/roster/999999", 404);
        await LogAsync(SmokeRun.UserAgent, "/history", 200);
        await LogAsync("Mozilla/5.0 (Macintosh)", "/history", 200);
        await LogAsync("Mozilla/5.0 (compatible; Googlebot/2.1)", "/wp-login.php", 404);

        var page = new AnalyticsModel(Db);
        await page.OnGetAsync(days: 7, bots: null, default);

        Assert.Equal(2, page.TotalRequests);
        Assert.Equal(2, page.OurOwnRequests);
        Assert.Equal(1, page.HumanRequests);

        Assert.DoesNotContain(page.TopMisses, m => m.Key == "/roster/999999");
        Assert.Contains(page.TopMisses, m => m.Key == "/wp-login.php");
        Assert.Equal(1, page.TopPaths.Single(p => p.Key == "/history").Total);
    }

    [Fact]
    public async Task Including_bots_still_leaves_our_own_run_out()
    {
        await LogAsync(SmokeRun.UserAgent, "/no-such-page", 404);
        await LogAsync("Mozilla/5.0 (compatible; Googlebot/2.1)", "/", 200);

        var page = new AnalyticsModel(Db);
        await page.OnGetAsync(days: 7, bots: true, default);

        Assert.False(page.ExcludeBots);
        Assert.Equal(1, page.TotalRequests);
        Assert.DoesNotContain(page.TopMisses, m => m.Key == "/no-such-page");
    }

    [Fact]
    public async Task A_request_of_ours_is_still_recorded()
    {
        // Marked, not skipped. A header that keeps a request out of the log is a
        // blind spot anybody can walk through by setting one header.
        await LogAsync(SmokeRun.UserAgent, "/", 200);

        Assert.True(await Db.RequestLogs.AnyAsync(r => r.UserAgent == SmokeRun.UserAgent));
    }
}

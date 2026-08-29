using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

namespace Rmv.Web.Herald;

/// <summary>
/// Checks whether the servers the guild currently plays on are answering.
///
/// Only active games with a herald we still have an adapter for. A game nobody
/// plays does not need a light on the wall, and a game with no herald has no
/// address to check.
///
/// The address is the one the character fetches use, so an admin's override is
/// honoured and the FFXI herald is reachable for the same reason its API is: the
/// operator allowlist permits its private address.
///
/// Every ten minutes, one request each, and the body is never read. That is 144
/// requests a day per server against someone else's front page, which is the most
/// this is worth spending.
/// </summary>
public sealed class ServerStatusMonitor(
    IServiceScopeFactory scopes,
    DatabaseState database,
    ServerStatusState state,
    ILogger<ServerStatusMonitor> log) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Period = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);

            using var timer = new PeriodicTimer(Period);
            do
            {
                await CheckAsync(ct);
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Public so a test can run one pass without waiting on a timer.</summary>
    public async Task<IReadOnlyList<ServerStatus>> CheckAsync(CancellationToken ct)
    {
        if (database.Status != DatabaseStatus.Ready)
        {
            // Nothing to check against yet. The previous answer stays on the wall
            // rather than being replaced with a false outage.
            return state.All;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RmvDbContext>();
            var heralds = scope.ServiceProvider.GetRequiredService<HeraldRegistry>();
            var fetcher = scope.ServiceProvider.GetRequiredService<HeraldFetcher>();

            var games = await db.GamePresences
                .Where(g => g.IsActive && g.HeraldAdapterKey != null)
                .OrderBy(g => g.SortOrder).ThenBy(g => g.Game)
                .AsNoTracking()
                .ToListAsync(ct);

            var results = new List<ServerStatus>(games.Count);

            foreach (var game in games)
            {
                if (heralds.Find(game.HeraldAdapterKey) is not { } adapter)
                {
                    continue;
                }

                var url = string.IsNullOrWhiteSpace(game.HeraldBaseUrl)
                    ? adapter.DefaultBaseUrl
                    : game.HeraldBaseUrl!;

                var (ok, ms, error) = await fetcher.PingAsync(url, ct);
                results.Add(new ServerStatus(game.Game, ok, ms, DateTimeOffset.UtcNow, error));
            }

            state.Set(results);
            return results;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Server status check failed.");
            return state.All;
        }
    }
}

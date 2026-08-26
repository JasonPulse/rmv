using System.Collections.Concurrent;
using Rmv.Web.Data;

namespace Rmv.Web.Analytics;

/// <summary>
/// Buffers request records and flushes them in batches on a background loop.
///
/// A request must not wait on a database write to finish rendering, and one
/// INSERT per request would be the busiest query on the site by a wide margin.
/// The queue is bounded and drops on overflow: losing analytics rows under load
/// is always preferable to slowing down or failing the actual request.
/// </summary>
public sealed class RequestLogWriter(
    IServiceScopeFactory scopes,
    DatabaseState state,
    ILogger<RequestLogWriter> log) : BackgroundService
{
    private const int QueueCapacity = 10_000;
    private const int BatchSize = 200;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentQueue<RequestLog> _queue = new();
    private int _dropped;

    /// <summary>Non-blocking. Called on the request path, so it must never throw or wait.</summary>
    public void Enqueue(RequestLog entry)
    {
        if (_queue.Count >= QueueCapacity)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        _queue.Enqueue(entry);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(FlushInterval);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(ct);
                await FlushAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Analytics must never take the site down. Log and keep going;
                // the rows still in the queue get another attempt next tick.
                log.LogWarning(ex, "Could not flush request logs.");
            }
        }

        // Best effort on shutdown, with a fresh token: ct is already cancelled.
        using var final = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await FlushAsync(final.Token);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not flush request logs on shutdown.");
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        // Nothing to write to yet. Keep buffering rather than throwing.
        if (!state.IsReady || _queue.IsEmpty)
        {
            return;
        }

        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
        {
            log.LogWarning("Dropped {Count} request log entries: queue was full.", dropped);
        }

        var batch = new List<RequestLog>(BatchSize);
        while (batch.Count < BatchSize && _queue.TryDequeue(out var entry))
        {
            batch.Add(entry);
        }

        if (batch.Count == 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RmvDbContext>();
        db.RequestLogs.AddRange(batch);
        await db.SaveChangesAsync(ct);
    }
}

using Microsoft.EntityFrameworkCore;

namespace Rmv.Web.Data;

/// <summary>
/// Applies migrations and records this boot, in the background rather than
/// during startup.
///
/// Doing it inline at startup means a database that is down, or merely slower to
/// accept connections than the app, takes the whole container into a crash loop.
/// Here the site comes up immediately, /healthz/ready stays unhealthy until this
/// succeeds, and the attempt repeats with backoff.
/// </summary>
public sealed class DatabaseInitializer(
    IServiceScopeFactory scopes,
    DatabaseState state,
    IConfiguration config,
    ILogger<DatabaseInitializer> log) : BackgroundService
{
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<RmvDbContext>();

                await db.Database.MigrateAsync(ct);

                db.Deployments.Add(new Deployment
                {
                    Version = config["Build:Version"] ?? "local",
                    Host = System.Net.Dns.GetHostName(),
                    StartedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(ct);

                state.Set(DatabaseStatus.Ready);
                log.LogInformation("Postgres ready, migrations applied, boot recorded.");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Keep the message but not the stack trace: this is surfaced on a
                // public page, and a full trace there is noise at best.
                state.Set(DatabaseStatus.Failed, ex.GetBaseException().Message);

                var wait = Backoff[Math.Min(attempt, Backoff.Length - 1)];
                attempt++;
                log.LogWarning(ex,
                    "Postgres not ready (attempt {Attempt}). Retrying in {Wait}.", attempt, wait);

                try
                {
                    await Task.Delay(wait, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}

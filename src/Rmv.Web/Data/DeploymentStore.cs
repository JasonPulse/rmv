using Microsoft.EntityFrameworkCore;

namespace Rmv.Web.Data;

/// <summary>What the status panel needs, whether or not Postgres exists.</summary>
public record StatusView(
    DatabaseStatus Status,
    string? Detail,
    Deployment? Current,
    int BootCount,
    DateTimeOffset CheckedAt);

public interface IDeploymentStore
{
    Task<StatusView> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Registered when no connection string is set. Lets the site run, and every
/// page render, with no database at all.
/// </summary>
public sealed class NullDeploymentStore : IDeploymentStore
{
    public Task<StatusView> ReadAsync(CancellationToken ct) => Task.FromResult(
        new StatusView(
            DatabaseStatus.NotConfigured,
            "ConnectionStrings__Postgres is not set.",
            Current: null,
            BootCount: 0,
            CheckedAt: DateTimeOffset.UtcNow));
}

public sealed class PostgresDeploymentStore(RmvDbContext db, DatabaseState state) : IDeploymentStore
{
    public async Task<StatusView> ReadAsync(CancellationToken ct)
    {
        // Do not query while the initializer is still working: the table may not
        // exist yet, and a failed query here would look like a different fault.
        if (!state.IsReady)
        {
            return new StatusView(state.Status, state.Detail, null, 0, DateTimeOffset.UtcNow);
        }

        try
        {
            var current = await db.Deployments
                .OrderByDescending(d => d.StartedAt)
                .AsNoTracking()
                .FirstOrDefaultAsync(ct);

            return new StatusView(
                DatabaseStatus.Ready,
                Detail: null,
                current,
                await db.Deployments.CountAsync(ct),
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Postgres went away after a successful start, for instance a restart
            // of the db container. Report it rather than returning a 500.
            var message = ex.GetBaseException().Message;
            state.Set(DatabaseStatus.Failed, message);
            return new StatusView(DatabaseStatus.Failed, message, null, 0, DateTimeOffset.UtcNow);
        }
    }
}

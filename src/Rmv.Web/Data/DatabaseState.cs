namespace Rmv.Web.Data;

public enum DatabaseStatus
{
    /// <summary>No connection string. The site runs, nothing reads Postgres.</summary>
    NotConfigured,

    /// <summary>Configured, migrations not applied yet. The initializer is retrying.</summary>
    Starting,

    /// <summary>Migrations applied, reads and writes are expected to work.</summary>
    Ready,

    /// <summary>Configured but unreachable or migration failed. Detail holds why.</summary>
    Failed,
}

/// <summary>
/// Shared, mutable view of whether Postgres is usable. Registered as a singleton
/// and written only by <see cref="DatabaseInitializer"/>.
///
/// This exists so a database that is missing, slow to start, or restarting does
/// not take the whole site down. Pages render either way; the ones that need
/// Postgres say what is wrong instead of throwing.
/// </summary>
public sealed class DatabaseState(DatabaseStatus initial)
{
    private readonly object _gate = new();
    private DatabaseStatus _status = initial;
    private string? _detail;
    private DateTimeOffset? _changedAt;

    public DatabaseStatus Status { get { lock (_gate) return _status; } }
    public string? Detail { get { lock (_gate) return _detail; } }
    public DateTimeOffset? ChangedAt { get { lock (_gate) return _changedAt; } }

    public bool IsConfigured => Status is not DatabaseStatus.NotConfigured;
    public bool IsReady => Status is DatabaseStatus.Ready;

    public void Set(DatabaseStatus status, string? detail = null)
    {
        lock (_gate)
        {
            _status = status;
            _detail = detail;
            _changedAt = DateTimeOffset.UtcNow;
        }
    }
}

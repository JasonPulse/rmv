namespace Rmv.Web.Herald;

/// <summary>One server, as of the last check.</summary>
/// <param name="Game">The game's name, so the panel reads in the guild's terms.</param>
/// <param name="Ms">Round trip in milliseconds.</param>
/// <param name="Error">Why it is down, when it is. Null when up.</param>
public sealed record ServerStatus(
    string Game, bool Ok, int Ms, DateTimeOffset CheckedAt, string? Error);

/// <summary>
/// The last status check, held in memory rather than in Postgres.
///
/// Deliberate. "Is the server up right now" is worth nothing after a restart, so
/// there is nothing to persist, and keeping it out of the database is what lets the
/// home page show it while still reading no database at all. Two replicas each
/// answer for themselves, which is the honest answer to "can we reach it from
/// here".
/// </summary>
public sealed class ServerStatusState
{
    private readonly object _gate = new();
    private IReadOnlyList<ServerStatus> _all = [];

    public IReadOnlyList<ServerStatus> All
    {
        get { lock (_gate) { return _all; } }
    }

    public void Set(IReadOnlyList<ServerStatus> all)
    {
        lock (_gate) { _all = all; }
    }
}

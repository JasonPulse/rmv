namespace Rmv.Web.Data;

/// <summary>
/// A character on a game server, owned by the member who added it.
///
/// The stats are a cached copy of what the herald said. They are not the source
/// of truth and are allowed to be stale; LastFetchedAt says how stale.
/// </summary>
public class Character
{
    public int Id { get; set; }

    public int MemberId { get; set; }

    public Member? Member { get; set; }

    public int GamePresenceId { get; set; }

    public GamePresence? Game { get; set; }

    public string Name { get; set; } = "";

    // --- copied from the herald ---------------------------------------------

    public string? Guild { get; set; }

    public string? Realm { get; set; }

    public string? Class { get; set; }

    public string? Race { get; set; }

    public int? Level { get; set; }

    /// <summary>As the herald writes it, e.g. "8L0", or an FFXI title.</summary>
    public string? RealmRank { get; set; }

    /// <summary>Realm points on DAoC; total job levels on FFXI.</summary>
    public long? Score { get; set; }

    public long? Kills { get; set; }

    public long? Deaths { get; set; }

    public string? LastOnline { get; set; }

    public string? HeraldUrl { get; set; }

    // --- bookkeeping ---------------------------------------------------------

    public DateTimeOffset AddedAt { get; set; }

    public DateTimeOffset? LastFetchedAt { get; set; }

    /// <summary>Set when the most recent refresh failed. Cleared on success.</summary>
    public string? LastError { get; set; }
}

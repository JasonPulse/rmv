namespace Rmv.Web.Data;

/// <summary>Where a character's stats came from, which decides who may change them.</summary>
public enum CharacterSource
{
    /// <summary>Fetched from the game's herald. Refreshable, and not hand-editable.</summary>
    Herald,

    /// <summary>Typed in by the owner, because the game has no herald to ask.</summary>
    Manual,
}

/// <summary>
/// A character on a game server, owned by the member who added it.
///
/// For a herald character the stats are a cached copy of what the herald said.
/// They are not the source of truth and are allowed to be stale; LastFetchedAt
/// says how stale. For a manual character the row IS the source of truth, so the
/// owner edits it and nothing ever refreshes it.
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

    /// <summary>
    /// Set when we hold a portrait for this character, and changes only when the
    /// picture does, because it is a digest of the picture; see
    /// CharacterService.VersionOf. Nothing a herald says about its own renders is
    /// trusted for this, having been wrong on all three.
    ///
    /// The bytes live in CharacterPortrait, in their own table, so they are not
    /// dragged along by every query that reads a character.
    /// </summary>
    public string? PortraitVersion { get; set; }

    public CharacterPortrait? Portrait { get; set; }

    /// <summary>
    /// Where a page points an img, or null when we have no picture.
    ///
    /// Our own route, always, for every herald. The FFXI herald is internal and a
    /// browser cannot reach it; the Lodestone could be linked directly but its URL
    /// carries a cache-buster that goes stale. The version is in the query so the
    /// browser refetches when the picture changes and never otherwise.
    /// </summary>
    public string? PortraitPath => PortraitVersion is null
        ? null
        : PortraitEndpoint.PathFor(Id, PortraitVersion);

    // --- bookkeeping ---------------------------------------------------------

    public CharacterSource Source { get; set; }

    /// <summary>A manual character has no herald to ask, so nothing refreshes it.</summary>
    public bool IsManual => Source == CharacterSource.Manual;

    public DateTimeOffset AddedAt { get; set; }

    public DateTimeOffset? LastFetchedAt { get; set; }

    /// <summary>Set when the most recent refresh failed. Cleared on success.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Whatever this character's own herald publishes beyond the columns above, as
    /// JSON, keyed by token name.
    ///
    /// A column per stat is not possible: the DAoC herald has seventy numbers, the
    /// FFXI API has a hundred fields, and each server's are its own. A document
    /// keeps the schema out of it and the editor's palette knows what to expect
    /// from IHeraldAdapter.Stats rather than from this.
    ///
    /// Null for a hand-typed character and for a herald with nothing extra.
    /// </summary>
    public string? Stats { get; set; }
}

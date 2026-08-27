namespace Rmv.Web.Data;

/// <summary>
/// One game the guild has played, and under which tags. "Where were we, where
/// are we now."
/// </summary>
public class GamePresence
{
    public int Id { get; set; }

    /// <summary>e.g. "Blackthorn DAoC", "Final Fantasy XI".</summary>
    public string Game { get; set; } = "";

    /// <summary>
    /// Free text, as it reads on the page: "RMV, Legends, Dark Auspices". Kept as
    /// one field rather than a child table because that is how it is written and
    /// how it is edited; splitting on commas for display is a view concern.
    /// </summary>
    public string Guilds { get; set; } = "";

    /// <summary>Optional, e.g. "2001-2012".</summary>
    public string? Period { get; set; }

    public bool IsActive { get; set; }

    /// <summary>Ascending within each of the active and inactive lists.</summary>
    public int SortOrder { get; set; }

    public List<GameLink> Links { get; set; } = [];

    public List<Character> Characters { get; set; } = [];

    /// <summary>
    /// Which herald adapter handles this game, e.g. "blackthorn". Null means
    /// characters cannot be added: there is nowhere to look them up.
    /// </summary>
    public string? HeraldAdapterKey { get; set; }

    /// <summary>
    /// Optional override for the adapter's own address, for the day a server
    /// changes domain. Normally null: the adapter knows where its herald lives.
    /// </summary>
    public string? HeraldBaseUrl { get; set; }

    /// <summary>
    /// Whether an adapter has been chosen. Whether that adapter actually exists
    /// is a question for the registry, not for the row.
    /// </summary>
    public bool HasHerald => !string.IsNullOrWhiteSpace(HeraldAdapterKey);

    public IEnumerable<string> GuildList() => Guilds
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

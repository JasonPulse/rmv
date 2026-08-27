namespace Rmv.Web.Herald;

/// <summary>
/// What a herald can tell us about one character. Every field is optional
/// because heralds differ and pages change; a parser that insists on everything
/// fails entirely when one row moves.
/// </summary>
public sealed record HeraldCharacter
{
    public required string Name { get; init; }

    public string? Guild { get; init; }

    public string? Realm { get; init; }

    public string? Class { get; init; }

    public string? Race { get; init; }

    public int? Level { get; init; }

    /// <summary>As the herald writes it, e.g. "8L0". Not parsed into numbers.</summary>
    public string? RealmRank { get; init; }

    public long? RealmPoints { get; init; }

    public long? Kills { get; init; }

    public long? Deaths { get; init; }

    /// <summary>Free text, e.g. "Recently". Heralds rarely give a real timestamp.</summary>
    public string? LastOnline { get; init; }

    /// <summary>The character's page, for linking straight to the herald.</summary>
    public string? Url { get; init; }
}

public sealed record HeraldResult(bool Ok, HeraldCharacter? Character, string? Error)
{
    public static HeraldResult Fail(string error) => new(false, null, error);
    public static HeraldResult Found(HeraldCharacter character) => new(true, character, null);
}

/// <summary>
/// One private server's herald. Code per server rather than configuration,
/// because no two of them are laid out the same way and some are not HTML at all.
/// </summary>
public interface IHeraldAdapter
{
    /// <summary>Stored against the Game row, so an admin picks the adapter.</summary>
    string Key { get; }

    string DisplayName { get; }

    /// <summary>What an admin should paste in, shown next to the field.</summary>
    string BaseUrlHint { get; }

    /// <summary>
    /// Fetches one character. Returns a failure rather than throwing for anything
    /// the operator could reasonably have got wrong: unknown name, herald down,
    /// page not shaped as expected.
    /// </summary>
    Task<HeraldResult> FetchCharacterAsync(string baseUrl, string characterName, CancellationToken ct);
}

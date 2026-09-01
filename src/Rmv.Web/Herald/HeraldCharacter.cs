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

    /// <summary>The character's portrait, if the herald has one.</summary>
    public HeraldPortrait? Portrait { get; init; }

    /// <summary>
    /// Whatever else this herald publishes, keyed by the token name that draws it.
    ///
    /// The twelve fields above are the ones every herald has something like, which
    /// is what makes %Level% and %Score% work whichever game a line is about. This
    /// is for the rest, and the rest is most of it: the DAoC herald publishes
    /// fourteen stats across five periods, seventy numbers, and three of them fit
    /// above. Realm points for last week was a token in the 2001 generator and had
    /// no home here until this existed.
    ///
    /// Already formatted, because a herald knows what its own numbers mean: seconds
    /// of playtime become "41 days", a ratio keeps its decimal, and a count gets
    /// its thousands separators. See IHeraldAdapter.Stats for what each one declares.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Stats { get; init; }
}

/// <summary>
/// A portrait a herald will serve us.
///
/// The bytes are fetched by the server and stored, not linked from the page, and
/// the reason is not caution. The FFXI herald is internal: it resolves to an
/// RFC1918 address, so a visitor's browser cannot reach it at all. Only the pod
/// can. Doing the same for the public heralds is not extra work, it is less: the
/// Lodestone's image URL carries a cache-buster that changes whenever a character
/// re-renders, so a stored link goes stale and 404s.
///
/// A URL and nothing else. There used to be a Version here as well, taken from
/// whatever each herald published about the appearance, and used to skip the
/// download when it had not changed. Every herald lied about it in its own way:
///
///   The FFXI herald served two visibly different renders of one character under
///   one appearance hash, and sent that hash as an immutable ETag.
///   Blizzard's render URL carries the character id, not the appearance, so it is
///   reused when someone changes armour.
///   The Lodestone's cache-buster changes when nothing about the picture has.
///
/// So the picture's version is now the picture. See
/// CharacterService.SyncPortraitAsync: the bytes are fetched and digested, and
/// that digest is the version. One image fetch per character per daily pass buys
/// a site that cannot show yesterday's armour, and no herald has to be believed
/// about anything.
/// </summary>
/// <param name="Url">Where the server fetches it from. Never sent to a browser.</param>
public sealed record HeraldPortrait(string Url);

public sealed record HeraldResult(bool Ok, HeraldCharacter? Character, string? Error)
{
    public static HeraldResult Fail(string error) => new(false, null, error);
    public static HeraldResult Found(HeraldCharacter character) => new(true, character, null);
}

/// <summary>
/// One private server's herald. Code per server rather than configuration,
/// because no two of them are laid out the same way and some are not HTML at all.
/// </summary>
/// <summary>What a herald's characters are ranked by on the leaderboards.</summary>
public enum RankBy
{
    /// <summary>Character.Score: realm points on DAoC, total job levels on FFXI.</summary>
    Score,

    /// <summary>Character.Level, for a herald that publishes no cumulative measure.</summary>
    Level,
}

/// <summary>
/// The measure a herald's characters are ranked by, and what to call it on screen.
///
/// Owned by the adapter for the same reason DefaultBaseUrl is: an adapter is code
/// written against one server, so it knows what that server measures. Asking an
/// admin to choose would be one more field to get wrong, and the wrong answer here
/// is a leaderboard that ranks on a column the herald never fills in.
/// </summary>
/// <param name="Label">Column heading, e.g. "Realm points".</param>
public sealed record LeaderboardMetric(RankBy By, string Label);

/// <summary>
/// Which of the shared columns a herald's stat reads.
///
/// Most stats are in the Stats document, which is what Document means. The rest are
/// the columns every herald fills in something like, and the point of naming them
/// here is that no two heralds mean the same thing by one: Score is realm points on
/// DAoC, total job levels on FFXI and achievement points on WoW, and Rank is a realm
/// rank on one and a title on the other two. A member offered a bare %Score% has to
/// guess. Declared per herald, each with its own label, they read as what they are.
/// </summary>
public enum SheetField
{
    /// <summary>From the character's Stats document, which is the usual case.</summary>
    Document,

    Guild,
    Realm,
    Rank,
    Score,
    Kills,
    Deaths,
    Seen,
}

/// <summary>
/// The token name each shared column is written as.
///
/// Fixed and complete here rather than accumulated as adapters declare their stats,
/// and that is the whole point: adapters are scoped services, built when a request
/// needs one. A signature served from the stored render never builds one, so a
/// resolver that learned these names from adapter construction would draw them for
/// one request and leave "%Score%" as typed for the next.
///
/// A herald chooses which of these it offers and what to call it; see
/// IHeraldAdapter.Stats. It does not get to point one at a different column, which
/// HeraldStatTokens.Declare enforces.
/// </summary>
public static class SheetColumns
{
    private static readonly Dictionary<string, SheetField> ByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Guild"] = SheetField.Guild,
            ["Realm"] = SheetField.Realm,
            ["Rank"] = SheetField.Rank,
            ["Score"] = SheetField.Score,
            ["Kills"] = SheetField.Kills,
            ["Deaths"] = SheetField.Deaths,
            ["Seen"] = SheetField.Seen,
        };

    /// <summary>The column this name reads, or null when it names no column.</summary>
    public static SheetField? Field(string key) =>
        ByName.TryGetValue(key, out var field) ? field : null;

    public static IEnumerable<string> Names => ByName.Keys;
}

/// <summary>
/// One stat a herald publishes, and what a signature calls it.
/// </summary>
/// <param name="Key">The token name, without percent signs. Unique across the herald.</param>
/// <param name="Label">
/// What the editor's palette calls it, in this herald's own words. Two heralds may
/// share a key and label it differently; that is the whole reason the label is here
/// and not in one shared list.
/// </param>
/// <param name="Example">A filled-in value, so the palette can show what it looks like.</param>
/// <param name="From">
/// The shared column this reads, or Document for the usual case. A key may only ever
/// name one column, whichever heralds declare it; HeraldStatTokens.Declare enforces
/// that at startup.
/// </param>
public sealed record HeraldStat(
    string Key, string Label, string Example, SheetField From = SheetField.Document);

public interface IHeraldAdapter
{
    /// <summary>How this server's characters compare. See LeaderboardMetric.</summary>
    LeaderboardMetric Metric { get; }

    /// <summary>
    /// What this herald publishes beyond the fields every herald has.
    ///
    /// Declared rather than discovered, because the signature editor has to offer
    /// these to a member who has no character on this server yet, and because a
    /// token the palette offers and no herald fills is a signature that says
    /// "%Relics%" to a forum.
    ///
    /// Empty for a herald with nothing extra. The keys must not collide with the
    /// tokens every character has; SignatureTokenTests holds that.
    /// </summary>
    IReadOnlyList<HeraldStat> Stats => [];

    /// <summary>
    /// Who this herald leaves out, in words a member reads, or null when it lists
    /// every character on its server.
    ///
    /// One property, because it answers two questions that must not disagree:
    /// whether the add form offers to skip the lookup and type the sheet in
    /// instead, and what it says beside that offer. The Armory is the reason it
    /// exists. It only shows characters on a subscribed account, and it answers a
    /// lapsed one exactly as it answers a misspelling, so "look it up" cannot be
    /// the only way to record a WoW character.
    ///
    /// Null is the normal case and keeps the normal rule: the game decides how its
    /// characters are recorded, not the member. See CharacterService.AddAsync.
    /// </summary>
    string? CoverageNote => null;

    /// <summary>Stored against the Game row, so an admin picks the adapter.</summary>
    string Key { get; }

    string DisplayName { get; }

    /// <summary>
    /// The server this adapter was written for.
    ///
    /// Authoritative, not a hint. An adapter is code targeting one specific
    /// herald's markup or API, so it cannot work against a different address.
    /// Asking an admin to retype it added a way to get it wrong and nothing else.
    /// A game may still override it, for the day a server changes domain.
    /// </summary>
    string DefaultBaseUrl { get; }

    /// <summary>
    /// Fetches one character. Returns a failure rather than throwing for anything
    /// the operator could reasonably have got wrong: unknown name, herald down,
    /// page not shaped as expected.
    /// </summary>
    Task<HeraldResult> FetchCharacterAsync(string baseUrl, string characterName, CancellationToken ct);
}

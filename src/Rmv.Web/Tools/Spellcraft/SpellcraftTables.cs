namespace Rmv.Web.Tools.Spellcraft;

/// <summary>Whether a bonus reads as a flat amount or a percentage.</summary>
public enum BonusUnit
{
    /// <summary>A flat amount, rendered "+26".</summary>
    Points,

    /// <summary>A percentage, rendered "+5%".</summary>
    Percent,
}

/// <summary>
/// How the gems in one item combine into the crafting skill the item needs.
///
/// Two shapes, because those are the two the rule could plausibly take and
/// nobody has told us which yet. See docs/spellcraft.md; this is one of the
/// answers Jason still owes the calculator.
/// </summary>
public enum SkillCombination
{
    /// <summary>The item needs whatever its most demanding gem needs.</summary>
    HighestGem,

    /// <summary>The item needs the sum of what every gem needs.</summary>
    TotalOfGems,
}

/// <summary>
/// A number that varies with the level of the item it is written on.
///
/// Both the imbue capacity of an item and the cap on a single bonus work this
/// way, so they share one shape. A value that does not vary is a table with one
/// row, which is why nothing here has to know whether the real rule is a curve or
/// a constant.
///
/// Levels between rows take the value of the nearest row at or below them, so a
/// table only needs a row where the number actually changes.
/// </summary>
public sealed class LevelTable
{
    private readonly (int Level, decimal Value)[] _rows;

    private LevelTable((int Level, decimal Value)[] rows) => _rows = rows;

    /// <summary>The same value at every item level.</summary>
    public static LevelTable Flat(decimal value) => new([(0, value)]);

    /// <summary>
    /// One row per level at which the number changes. Order does not matter; the
    /// rows are sorted here.
    /// </summary>
    public static LevelTable ByLevel(IEnumerable<(int Level, decimal Value)> rows)
    {
        var sorted = rows.OrderBy(r => r.Level).ToArray();

        if (sorted.Length == 0)
        {
            throw new ArgumentException("A level table needs at least one row.", nameof(rows));
        }

        return new LevelTable(sorted);
    }

    /// <summary>
    /// The value at an item level. Below the lowest row the lowest row's value is
    /// used, so the table cannot produce a hole.
    /// </summary>
    public decimal At(int itemLevel)
    {
        var value = _rows[0].Value;

        foreach (var row in _rows)
        {
            if (row.Level > itemLevel)
            {
                break;
            }

            value = row.Value;
        }

        return value;
    }
}

/// <summary>One of the game's realms. Restricts which gems and slots are offered.</summary>
public sealed record Realm(string Code, string Name);

/// <summary>
/// A thing a gem can add to an item, and how much of it one item may carry.
///
/// The cap is per item level because that is the most general shape the real rule
/// can take. If it turns out to be a single number, the table has one row.
/// </summary>
public sealed record BonusKind(string Code, string Name, BonusUnit Unit, LevelTable Cap);

/// <summary>
/// One gem, at one quality, granting one bonus.
///
/// A gem is a whole row rather than a quality crossed with a bonus, because the
/// two are not independent in the real tables: not every bonus exists at every
/// quality, and the imbue cost is per row. Modelling it as a product would make
/// combinations that do not exist representable.
/// </summary>
public sealed record Gem(
    string Code,
    string Name,
    // The quality tier this gem sits at, for grouping the picker.
    string Quality,
    string BonusCode,
    // How much of the bonus this gem grants. Always positive.
    int Amount,
    // What this gem spends of the item's imbue capacity. Fractional values are allowed.
    decimal ImbuePoints,
    // The spellcraft skill this gem alone demands.
    int SkillRequired,
    // The realm this gem belongs to, or null when every realm has it.
    string? RealmCode);

/// <summary>
/// A place on a character an item goes, and what that item can hold.
///
/// Imbue capacity hangs off the slot rather than off the item, so a rule that
/// differs between armour, weapons and jewellery needs no reshaping. If capacity
/// turns out to be the same everywhere, every slot carries the same table.
/// </summary>
public sealed record ItemSlot(
    string Code,
    string Name,
    // How many gems this item takes.
    int Sockets,
    LevelTable ImbueCapacity,
    // Bonuses this slot accepts. Empty means it accepts every bonus.
    IReadOnlyList<string> AllowedBonusCodes,
    // The realm this slot belongs to, or null when every realm has it.
    string? RealmCode);

/// <summary>
/// The chance of surviving a given number of imbue points over the item's
/// capacity. One row per whole point over.
/// </summary>
public sealed record OverchargeStep(int PointsOver, int SuccessPercent);

/// <summary>
/// Everything the calculator knows about the game. No behaviour, only numbers.
///
/// Validated on construction, so a table with a gem pointing at a bonus that does
/// not exist fails at startup rather than on somebody's page. Nothing downstream
/// re-checks those invariants.
///
/// Verified says whether these numbers came from the game or are the sample set
/// shipped to build the page against. It is deliberately required, so a new table
/// cannot be added without someone answering the question, and it is what the page
/// reads to decide whether to warn the visitor.
/// </summary>
public sealed class SpellcraftTables
{
    /// <summary>
    /// Bounds the encoded form of a design, and with it the database column. No
    /// item in any game the guild has played takes anything close to this.
    /// </summary>
    public const int MaxSockets = 8;

    public required bool Verified { get; init; }

    /// <summary>Where these numbers came from, shown on the page.</summary>
    public required string SourceNote { get; init; }

    public required IReadOnlyList<Realm> Realms { get; init; }

    public required IReadOnlyList<BonusKind> Bonuses { get; init; }

    public required IReadOnlyList<Gem> Gems { get; init; }

    public required IReadOnlyList<ItemSlot> Slots { get; init; }

    /// <summary>Ordered by PointsOver. Empty means overcharging is not possible at all.</summary>
    public required IReadOnlyList<OverchargeStep> Overcharge { get; init; }

    public required SkillCombination SkillRule { get; init; }

    public required int MinItemLevel { get; init; }

    public required int MaxItemLevel { get; init; }

    private Dictionary<string, BonusKind> _bonusByCode = null!;
    private Dictionary<string, Gem> _gemByCode = null!;
    private Dictionary<string, ItemSlot> _slotByCode = null!;

    /// <summary>
    /// Checks the set hangs together and builds the lookups. Throws rather than
    /// reporting, because only our own code builds a table and a broken one is a
    /// bug, not bad input.
    /// </summary>
    public SpellcraftTables Validated()
    {
        _bonusByCode = Index(Bonuses, b => b.Code, "bonus");
        _gemByCode = Index(Gems, g => g.Code, "gem");
        _slotByCode = Index(Slots, s => s.Code, "slot");
        var realms = Index(Realms, r => r.Code, "realm");

        if (MinItemLevel < 1 || MaxItemLevel < MinItemLevel)
        {
            throw new InvalidOperationException("Item levels have to run from at least 1, upwards.");
        }

        foreach (var gem in Gems)
        {
            Require(_bonusByCode.ContainsKey(gem.BonusCode), $"Gem {gem.Code} grants unknown bonus {gem.BonusCode}.");
            Require(gem.RealmCode is null || realms.ContainsKey(gem.RealmCode), $"Gem {gem.Code} is in unknown realm {gem.RealmCode}.");
            Require(gem.Amount > 0, $"Gem {gem.Code} grants nothing.");
            Require(gem.ImbuePoints >= 0, $"Gem {gem.Code} has a negative imbue cost.");
            Require(gem.SkillRequired >= 0, $"Gem {gem.Code} has a negative skill requirement.");
        }

        foreach (var slot in Slots)
        {
            Require(slot.Sockets is >= 1 and <= MaxSockets, $"Slot {slot.Code} has {slot.Sockets} sockets, outside 1 to {MaxSockets}.");
            Require(slot.RealmCode is null || realms.ContainsKey(slot.RealmCode), $"Slot {slot.Code} is in unknown realm {slot.RealmCode}.");

            foreach (var code in slot.AllowedBonusCodes)
            {
                Require(_bonusByCode.ContainsKey(code), $"Slot {slot.Code} allows unknown bonus {code}.");
            }
        }

        var over = 0;
        foreach (var step in Overcharge)
        {
            Require(step.PointsOver > over, "Overcharge steps have to rise, one row per whole point over.");
            Require(step.SuccessPercent is >= 0 and <= 100, $"Overcharge at {step.PointsOver} over is not a percentage.");
            over = step.PointsOver;
        }

        return this;
    }

    public BonusKind? FindBonus(string? code) => Lookup(_bonusByCode, code);

    public Gem? FindGem(string? code) => Lookup(_gemByCode, code);

    public ItemSlot? FindSlot(string? code) => Lookup(_slotByCode, code);

    public Realm? FindRealm(string? code) =>
        Realms.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.Ordinal));

    /// <summary>
    /// Slots a realm can craft for: its own, plus any that belong to no realm in
    /// particular. Same rule for gems, below.
    /// </summary>
    public IReadOnlyList<ItemSlot> SlotsFor(Realm? realm) =>
        Slots.Where(s => s.RealmCode is null || s.RealmCode == realm?.Code).ToList();

    public IReadOnlyList<Gem> GemsFor(Realm? realm, ItemSlot slot) =>
        Gems.Where(g => (g.RealmCode is null || g.RealmCode == realm?.Code) && Accepts(slot, g))
            .ToList();

    /// <summary>A slot with no list accepts everything, which is the common case.</summary>
    public bool Accepts(ItemSlot slot, Gem gem) =>
        slot.AllowedBonusCodes.Count == 0 || slot.AllowedBonusCodes.Contains(gem.BonusCode);

    private static TValue? Lookup<TValue>(Dictionary<string, TValue> index, string? code)
        where TValue : class =>
        code is not null && index.TryGetValue(code, out var found) ? found : null;

    private static Dictionary<string, T> Index<T>(
        IReadOnlyList<T> rows, Func<T, string> code, string what)
    {
        var index = new Dictionary<string, T>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var key = code(row);
            Require(!string.IsNullOrWhiteSpace(key), $"A {what} has no code.");
            Require(index.TryAdd(key, row), $"Two {what} rows share the code {key}.");
        }

        return index;
    }

    private static void Require(bool ok, string message)
    {
        if (!ok)
        {
            throw new InvalidOperationException(message);
        }
    }
}

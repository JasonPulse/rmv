namespace Rmv.Web.Tools.Spellcraft;

/// <summary>
/// SAMPLE DATA. NONE OF THESE NUMBERS CAME FROM THE GAME.
///
/// Every value below was made up so the page, the calculator and the template
/// saving could be built and demonstrated end to end. Bonus amounts, imbue costs,
/// caps, capacities, skill requirements, overcharge chances, and which gem
/// belongs to which realm are all invented. A player following this would waste
/// materials.
///
/// It is marked in three places on purpose: Verified is false, every gem and slot
/// name carries the word "sample", and the page reads Verified to put a warning
/// above the form.
///
/// Replacing it is meant to be one file and one line in Program.cs. Nothing in
/// SpellcraftCalculator, the page, or the template store knows this class exists.
/// docs/spellcraft.md lists exactly which numbers are needed, field by field.
/// </summary>
public static class PlaceholderSpellcraftTables
{
    private const string Sample =
        "Sample data. These numbers were invented to build the page against and are "
        + "not the game's. Do not craft from them.";

    public static SpellcraftTables Build() => new SpellcraftTables
    {
        Verified = false,
        SourceNote = Sample,
        SkillRule = SkillCombination.HighestGem,
        MinItemLevel = 1,
        MaxItemLevel = 51,
        Realms = Realms(),
        Bonuses = Bonuses(),
        Slots = Slots(),
        Gems = Gems(),
        Overcharge = Overcharge(),
    }.Validated();

    /// <summary>The three realms are structural, not invented. What they may use is invented.</summary>
    private static IReadOnlyList<Realm> Realms() =>
    [
        new("alb", "Albion"),
        new("mid", "Midgard"),
        new("hib", "Hibernia"),
    ];

    private static IReadOnlyList<BonusKind> Bonuses() =>
    [
        new("str", "Strength", BonusUnit.Points, ByLevel(1, 5, 20, 15, 40, 26)),
        new("dex", "Dexterity", BonusUnit.Points, ByLevel(1, 5, 20, 15, 40, 26)),
        new("con", "Constitution", BonusUnit.Points, ByLevel(1, 5, 20, 15, 40, 26)),
        new("hits", "Hit points", BonusUnit.Points, ByLevel(1, 20, 20, 80, 40, 200)),
        new("body", "Body resist", BonusUnit.Percent, LevelTable.Flat(26)),
    ];

    private static IReadOnlyList<ItemSlot> Slots() =>
    [
        // Four sockets, every bonus allowed.
        new("chest", "Chest (sample)", 4, Capacity(), [], null),
        new("helm", "Helm (sample)", 3, Capacity(), [], null),
        // A slot that refuses a bonus, so the restriction path is exercised.
        new("ring", "Ring (sample)", 2, Capacity(), ["str", "dex", "con"], null),
        new("blade", "Blade (sample)", 4, Capacity(), [], null),
    ];

    private static IReadOnlyList<Gem> Gems() =>
    [
        ..Tier("1", 1, 6, 1.0m, 20),
        ..Tier("2", 2, 12, 2.5m, 500),
        ..Tier("3", 3, 22, 5.0m, 900),
        // One realm-locked gem, so the realm filter is exercised.
        new("dex-hib", "Dexterity (sample, Hibernia only)", "Sample tier 3", "dex", 26, 6.0m, 1000, "hib"),
    ];

    /// <summary>
    /// One row per bonus at one made-up quality. Written as a loop rather than
    /// fifteen literal rows so the invented numbers are visible in one place.
    /// </summary>
    private static IEnumerable<Gem> Tier(string tier, int rank, int amount, decimal imbue, int skill)
    {
        (string Code, string Name, int Scale)[] bonuses =
        [
            ("str", "Strength", 1),
            ("dex", "Dexterity", 1),
            ("con", "Constitution", 1),
            ("hits", "Hit points", 4),
            ("body", "Body resist", 0),
        ];

        foreach (var (code, name, scale) in bonuses)
        {
            // Resists are small numbers and hit points are large ones, so the
            // sample scales them rather than giving every bonus the same amount.
            var granted = scale == 0 ? rank * 2 : amount * scale;

            yield return new Gem(
                $"{code}-{tier}",
                $"{name} (sample tier {tier})",
                $"Sample tier {tier}",
                code,
                granted,
                imbue,
                skill,
                null);
        }
    }

    private static IReadOnlyList<OverchargeStep> Overcharge() =>
    [
        new(1, 99),
        new(2, 95),
        new(3, 80),
        new(4, 55),
        new(5, 25),
    ];

    /// <summary>Sample imbue capacity, the same for every slot until told otherwise.</summary>
    private static LevelTable Capacity() =>
        LevelTable.ByLevel([(1, 2m), (10, 8m), (20, 16m), (30, 24m), (40, 30m), (51, 37m)]);

    /// <summary>Three points on a cap curve, written as pairs so the shape is obvious.</summary>
    private static LevelTable ByLevel(int l1, decimal v1, int l2, decimal v2, int l3, decimal v3) =>
        LevelTable.ByLevel([(l1, v1), (l2, v2), (l3, v3)]);
}

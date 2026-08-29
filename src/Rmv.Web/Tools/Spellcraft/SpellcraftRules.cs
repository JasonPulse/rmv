namespace Rmv.Web.Tools.Spellcraft;

/// <summary>
/// The family a bonus belongs to. Decides what a gem of it costs to imbue and
/// which character-wide cap it counts against.
/// </summary>
public enum BonusFamily
{
    Stat,
    Resist,
    Hits,
    Power,
    Skill,
    Focus,
}

/// <summary>
/// The arithmetic of spellcrafting, transcribed from the server that runs it.
///
/// Every formula and table here comes from Dawn of Light's SpellCrafting.cs and
/// its property calculators, which is the emulator the classic freeshards are
/// built on, so this is the rule as executed rather than as remembered. Each one
/// is cross-checked against at least one independent community source; where the
/// two disagreed the disagreement is recorded in a comment rather than smoothed
/// over.
///
/// Nothing here is per-realm, per-slot or per-item-name. It is pure arithmetic,
/// so it is unit testable with no database, no HTTP and no clock.
///
/// See docs/spellcraft.md for the provenance of every number.
/// </summary>
public static class SpellcraftRules
{
    /// <summary>Every craftable item has four gem slots. Not a setting; the server hardcodes it.</summary>
    public const int SocketsPerItem = 4;

    /// <summary>Ten gem qualities, Raw through Perfect.</summary>
    public const int Tiers = 10;

    /// <summary>Item quality runs 94 to 100. Below 94 cannot be imbued at all.</summary>
    public const int MinItemQuality = 94;

    public const int MaxItemQuality = 100;

    /// <summary>Item level 51 is the highest the capacity table carries.</summary>
    public const int MaxItemLevel = 51;

    /// <summary>
    /// The furthest an item can be pushed past its capacity. The server refuses
    /// the combine beyond six, and reports a zero success chance beyond five, so
    /// five is the real limit.
    /// </summary>
    public const int MaxOvercharge = 5;

    /// <summary>
    /// What one gem costs of an item's imbue capacity.
    ///
    /// Transcribed from GetGemImbuePoints. The integer division in the stat case
    /// is deliberate and is not the same as dividing at the end: a stat gem of 7
    /// costs 5, not 5.
    ///
    /// The value passed in is the bonus as the game displays it. For stats that
    /// is the gem's internal level scaled by 1.5 and rounded down, which is why
    /// the stat sequence reads 1, 4, 7 rather than 1, 3, 5.
    /// </summary>
    public static int GemImbueCost(BonusFamily family, int value)
    {
        var cost = family switch
        {
            BonusFamily.Stat => ((value - 1) * 2 / 3) + 1,
            BonusFamily.Resist => (value * 2) - 2,
            BonusFamily.Power => (value * 2) - 2,
            BonusFamily.Hits => value / 4,
            BonusFamily.Skill => (value - 1) * 5,
            // Focus gems are free at every level, which is why focus pullers can
            // fill an item with them.
            BonusFamily.Focus => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown bonus family."),
        };

        return Math.Max(1, cost);
    }

    /// <summary>
    /// What an item's gems cost together, which is not their sum.
    ///
    /// The most expensive gem is counted twice and the whole is then halved and
    /// floored. So the biggest gem is paid for in full and every other gem is
    /// half price, which is why a four gem item is far cheaper than adding up its
    /// gems suggests, and why one huge gem beside three small ones is the
    /// efficient shape.
    ///
    /// From GetTotalImbuePoints. Independently described the same way by the
    /// Ars Magna guide as ((highest x 2) + 2nd + 3rd + 4th) / 2.
    /// </summary>
    public static int ItemImbueTotal(IReadOnlyCollection<int> gemCosts)
    {
        ArgumentNullException.ThrowIfNull(gemCosts);

        if (gemCosts.Count == 0)
        {
            return 0;
        }

        return (gemCosts.Sum() + gemCosts.Max()) / 2;
    }

    /// <summary>
    /// The spellcraft skill needed to imbue an item, from the server's check
    /// that the imbue total must not exceed skill / 20.
    /// </summary>
    public static int SkillToImbue(int imbueTotal) => imbueTotal * 20;

    /// <summary>
    /// The skill needed to craft one gem, from the recipe list: each tier starts
    /// a hundred higher than the last, offset by which element the gem is made
    /// of. A Perfect Fiery Essence Jewel is 900 plus Fiery's 18, which is the 918
    /// the recipe states.
    /// </summary>
    public static int SkillToCutGem(int tier, int elementOffset) =>
        ((tier - 1) * 100) + elementOffset;

    /// <summary>Tempers per gem: 1 at Raw, rising by four a tier to 37 at Perfect.</summary>
    public static int TempersFor(int tier) => 1 + ((tier - 1) * 4);

    /// <summary>Element reagents per gem: one a tier, so 1 at Raw and 10 at Perfect.</summary>
    public static int ReagentsFor(int tier) => tier;

    /// <summary>One power gem per spellcraft gem, whatever the tier.</summary>
    public const int PowerGemsPerGem = 1;

    // --- item capacity -------------------------------------------------------

    /// <summary>
    /// How many imbue points an item holds, by item level and quality.
    ///
    /// Straight from the server's itemMaxBonusLevel, which its own comment
    /// attributes to Mythic's calculator. Row 51 reads 10, 15, 18, 21, 24, 28, 32
    /// across qualities 94 to 100, and the 24, 28 and 32 there are the numbers
    /// players quote for 98, 99 and 100 percent items.
    /// </summary>
    private static readonly int[,] Capacity =
    {
        {0,1,1,1,1,1,1}, {1,1,1,1,1,2,2}, {1,1,1,2,2,2,2}, {1,1,2,2,2,3,3},
        {1,2,2,2,3,3,4}, {1,2,2,3,3,4,4}, {2,2,3,3,4,4,5}, {2,3,3,4,4,5,5},
        {2,3,3,4,5,5,6}, {2,3,4,4,5,6,7}, {2,3,4,5,6,6,7}, {3,4,4,5,6,7,8},
        {3,4,5,6,6,7,9}, {3,4,5,6,7,8,9}, {3,4,5,6,7,8,10}, {3,5,6,7,8,9,10},
        {4,5,6,7,8,10,11}, {4,5,6,8,9,10,12}, {4,6,7,8,9,11,12}, {4,6,7,8,10,11,13},
        {4,6,7,9,10,12,13}, {5,6,8,9,11,12,14}, {5,7,8,10,11,13,15}, {5,7,9,10,12,13,15},
        {5,7,9,10,12,14,16}, {5,8,9,11,12,14,16}, {6,8,10,11,13,15,17}, {6,8,10,12,13,15,18},
        {6,8,10,12,14,16,18}, {6,9,11,12,14,16,19}, {6,9,11,13,15,17,20}, {7,9,11,13,15,17,20},
        {7,10,12,14,16,18,21}, {7,10,12,14,16,19,21}, {7,10,12,14,17,19,22}, {7,10,13,15,17,20,23},
        {8,11,13,15,17,20,23}, {8,11,13,16,18,21,24}, {8,11,14,16,18,21,24}, {8,11,14,16,19,22,25},
        {8,12,14,17,19,22,26}, {9,12,15,17,20,23,26}, {9,12,15,18,20,23,27}, {9,13,15,18,21,24,27},
        {9,13,16,18,21,24,28}, {9,13,16,19,22,25,29}, {10,13,16,19,22,25,29}, {10,14,17,20,23,26,30},
        {10,14,17,20,23,27,31}, {10,14,17,20,23,27,31}, {10,15,18,21,24,28,32},
    };

    /// <summary>
    /// Imbue capacity for an item. Levels above 51 are treated as 51, which is
    /// what the server does.
    /// </summary>
    public static int ItemCapacity(int itemLevel, int quality)
    {
        if (itemLevel < 1)
        {
            return 0;
        }

        var level = Math.Min(itemLevel, MaxItemLevel);
        var q = Math.Clamp(quality, MinItemQuality, MaxItemQuality);

        return Capacity[level - 1, q - MinItemQuality];
    }

    // --- overcharge ----------------------------------------------------------

    /// <summary>How much of the success chance each point over costs.</summary>
    private static readonly int[] OverchargeStart = [0, 10, 20, 30, 50, 70];

    /// <summary>What item quality adds back, indexed from quality 94.</summary>
    private static readonly int[] QualityModifier = [0, 0, 6, 8, 10, 18, 26];

    /// <summary>
    /// The chance an overcharged imbue succeeds, as a percentage.
    ///
    /// From CalculateChanceToOverchargeItem, whose own comment credits Kort's
    /// Spellcrafting Calculator. Within capacity it is certain. More than five
    /// points over it is impossible, and the server returns zero rather than a
    /// small chance.
    ///
    /// The fudge term is negative until skill 1000, which is what makes the last
    /// stretch of crafting skill worth so much on a deep overcharge.
    /// </summary>
    public static int OverchargeChance(int pointsOver, int itemQuality, int crafterSkill)
    {
        if (pointsOver <= 0)
        {
            return 100;
        }

        if (pointsOver > MaxOvercharge)
        {
            return 0;
        }

        var quality = Math.Clamp(itemQuality, MinItemQuality, MaxItemQuality);
        var start = OverchargeStart[pointsOver];

        var success = 34 + QualityModifier[quality - MinItemQuality] - start;

        var skillBonus = Math.Min(Math.Max(crafterSkill, 0) / 10, 100);
        success += skillBonus;

        var fudge = (int)(100.0 * (((skillBonus / 100.0) - 1.0) * (start / 200.0)));
        success += fudge;

        return Math.Clamp(success, 0, 100);
    }

    // --- character-wide caps -------------------------------------------------

    /// <summary>
    /// The most of one bonus a character gets from equipment, whatever it is
    /// spread across. From the server's property calculators: StatCalculator uses
    /// level times 1.5, ResistCalculator and MaxManaCalculator use level over two
    /// plus one, MaxHealthCalculator uses level times four, and
    /// SkillLevelCalculator uses level over five plus one.
    ///
    /// This is the reason a spellcraft calculator has to work on a whole build.
    /// The cap is on the character, not on the item, so an item that looks fine
    /// alone can waste every point it carries once the rest of the set is on.
    /// </summary>
    public static int CharacterCap(BonusFamily family, int characterLevel) => family switch
    {
        BonusFamily.Stat => (int)(characterLevel * 1.5),
        BonusFamily.Resist => (characterLevel / 2) + 1,
        BonusFamily.Power => (characterLevel / 2) + 1,
        BonusFamily.Hits => characterLevel * 4,
        BonusFamily.Skill => (characterLevel / 5) + 1,
        // Focus is not capped by equipment the way the others are, so nothing
        // here claims a number for it.
        BonusFamily.Focus => int.MaxValue,
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown bonus family."),
    };
}

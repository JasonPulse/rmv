namespace Rmv.Web.Tools.Spellcraft;

/// <summary>
/// How much of one bonus the item ends up with, against how much it may carry.
///
/// Wasted is the part worth having: an item three points over the cap on
/// dexterity has paid imbue points for nothing, and that is the mistake a
/// spellcraft calculator exists to catch.
/// </summary>
public sealed record BonusTotal(BonusKind Bonus, int Total, int Cap)
{
    public bool OverCap => Total > Cap;

    public bool AtCap => Total == Cap;

    public int Wasted => Math.Max(0, Total - Cap);
}

/// <summary>
/// What happens when an item is imbued past its capacity.
///
/// SuccessPercent is null when the item is further over than the overcharge table
/// goes, which means it cannot be made at all rather than that it is risky.
/// </summary>
public sealed record OverchargeOutcome(decimal PointsOver, int? SuccessPercent)
{
    public static readonly OverchargeOutcome Safe = new(0m, 100);

    public bool IsOvercharged => PointsOver > 0m;

    public bool Impossible => SuccessPercent is null;
}

/// <summary>Everything the page shows about one item.</summary>
public sealed record SpellcraftReport(
    ItemSlot Slot,
    Realm? Realm,
    int ItemLevel,
    IReadOnlyList<SocketFill> Sockets,
    IReadOnlyList<BonusTotal> Bonuses,
    decimal ImbueUsed,
    decimal ImbueCapacity,
    int SkillRequired,
    OverchargeOutcome Overcharge,
    bool Verified)
{
    public int GemsUsed => Sockets.Count(s => s.Counts);

    public bool AnyOverCap => Bonuses.Any(b => b.OverCap);

    /// <summary>Sockets whose gem was rejected, in socket order.</summary>
    public IReadOnlyList<SocketFill> Rejected =>
        Sockets.Where(s => s.Problem is not null).ToList();

    /// <summary>Imbue points left before the item goes over. Negative once it has.</summary>
    public decimal ImbueLeft => ImbueCapacity - ImbueUsed;
}

/// <summary>
/// The arithmetic, and nothing else. No database, no HTTP, no clock.
///
/// Every number it reads comes from a SpellcraftTables, so replacing the sample
/// set with the real one changes what this returns without changing a line of it.
/// The one thing that is not a number is how gem skill requirements combine into
/// an item's, and that is a switch over SkillCombination rather than a formula
/// written into the totals, so answering that question is also a data change.
/// </summary>
public static class SpellcraftCalculator
{
    public static SpellcraftReport Evaluate(ResolvedDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);

        var tables = design.Tables;
        var counted = design.Sockets.Where(s => s.Counts).Select(s => s.Gem!).ToList();

        // Grouped by bonus rather than by gem: two gems of strength are one line
        // on the item, and the cap applies to their sum.
        var bonuses = counted
            .GroupBy(g => g.BonusCode, StringComparer.Ordinal)
            .Select(group => new
            {
                Bonus = tables.FindBonus(group.Key)!,
                Total = group.Sum(g => g.Amount),
            })
            .Select(x => new BonusTotal(x.Bonus, x.Total, (int)x.Bonus.Cap.At(design.ItemLevel)))
            .OrderBy(b => b.Bonus.Name, StringComparer.Ordinal)
            .ToList();

        var used = counted.Sum(g => g.ImbuePoints);
        var capacity = design.Slot.ImbueCapacity.At(design.ItemLevel);

        return new SpellcraftReport(
            design.Slot,
            design.Realm,
            design.ItemLevel,
            design.Sockets,
            bonuses,
            used,
            capacity,
            SkillFor(tables.SkillRule, counted),
            OverchargeFor(tables, used - capacity),
            tables.Verified);
    }

    private static int SkillFor(SkillCombination rule, IReadOnlyList<Gem> gems)
    {
        if (gems.Count == 0)
        {
            return 0;
        }

        return rule switch
        {
            SkillCombination.HighestGem => gems.Max(g => g.SkillRequired),
            SkillCombination.TotalOfGems => gems.Sum(g => g.SkillRequired),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown skill rule."),
        };
    }

    /// <summary>
    /// Looks the overage up in whole points, rounding up, because a fractional
    /// point over is still over. Past the end of the table the item cannot be
    /// made, which is reported as no chance rather than as the worst chance in
    /// the table.
    /// </summary>
    private static OverchargeOutcome OverchargeFor(SpellcraftTables tables, decimal over)
    {
        if (over <= 0m)
        {
            return OverchargeOutcome.Safe;
        }

        var whole = (int)Math.Ceiling(over);
        var step = tables.Overcharge.FirstOrDefault(s => s.PointsOver == whole);

        return new OverchargeOutcome(over, step?.SuccessPercent);
    }
}

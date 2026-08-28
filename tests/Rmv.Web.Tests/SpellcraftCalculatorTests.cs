using Rmv.Web.Tools.Spellcraft;

namespace Rmv.Web.Tests;

/// <summary>
/// The arithmetic, against a table written for the tests rather than against the
/// sample set the site ships. That is the point of the seam: none of these
/// assertions change when the real game numbers land, because none of them are
/// game numbers.
///
/// No database, no HTTP, no clock, so these run in CI.
/// </summary>
public class SpellcraftCalculatorTests
{
    // Deliberately small and deliberately odd. Nothing here resembles the game.
    private static SpellcraftTables Tables(SkillCombination rule = SkillCombination.HighestGem) =>
        new SpellcraftTables
        {
            Verified = true,
            SourceNote = "Written for the tests.",
            SkillRule = rule,
            MinItemLevel = 1,
            MaxItemLevel = 50,
            Realms = [new("red", "Red"), new("blue", "Blue")],
            Bonuses =
            [
                new("pow", "Power", BonusUnit.Points, LevelTable.Flat(20)),
                new("res", "Resist", BonusUnit.Percent, LevelTable.ByLevel([(1, 5m), (40, 26m)])),
            ],
            Slots =
            [
                new("two", "Two socket", 2, LevelTable.Flat(10), [], null),
                new("solo", "One socket", 1, LevelTable.Flat(3), [], null),
                new("powonly", "Power only", 2, LevelTable.Flat(10), ["pow"], null),
                new("redgear", "Red gear", 1, LevelTable.Flat(10), [], "red"),
            ],
            Gems =
            [
                new("p5", "Power five", "Low", "pow", 5, 2m, 100, null),
                new("p9", "Power nine", "Mid", "pow", 9, 5m, 400, null),
                new("p15", "Power fifteen", "High", "pow", 15, 6m, 800, null),
                new("r3", "Resist three", "Mid", "res", 3, 3.5m, 250, null),
                new("pred", "Power red", "Low", "pow", 7, 1m, 50, "red"),
            ],
            Overcharge = [new(1, 90), new(2, 50)],
        }.Validated();

    private static SpellcraftReport Run(
        string realm, string slot, int level, SkillCombination rule, params string[] gems)
    {
        var resolution = Tables(rule).Resolve(new SpellcraftDesign(realm, slot, level, gems));
        Assert.Null(resolution.Error);

        return SpellcraftCalculator.Evaluate(resolution.Design!);
    }

    private static SpellcraftReport Run(string slot, params string[] gems) =>
        Run("", slot, 50, SkillCombination.HighestGem, gems);

    private static BonusTotal Bonus(SpellcraftReport report, string name) =>
        report.Bonuses.Single(b => b.Bonus.Name == name);

    // --- totals --------------------------------------------------------------

    [Fact]
    public void Two_gems_of_one_bonus_add_together()
    {
        var report = Run("two", "p5", "p5");

        Assert.Equal(10, Bonus(report, "Power").Total);
        Assert.Equal(2, report.GemsUsed);
    }

    [Fact]
    public void An_empty_socket_contributes_nothing()
    {
        var report = Run("two", "p9", "");

        Assert.Equal(9, Bonus(report, "Power").Total);
        Assert.Equal(1, report.GemsUsed);
        // Still two sockets, one of them just has nothing in it.
        Assert.Equal(2, report.Sockets.Count);
        Assert.Empty(report.Rejected);
    }

    [Fact]
    public void An_item_with_nothing_in_it_totals_nothing()
    {
        var report = Run("two");

        Assert.Empty(report.Bonuses);
        Assert.Equal(0m, report.ImbueUsed);
        Assert.Equal(0, report.SkillRequired);
        Assert.False(report.Overcharge.IsOvercharged);
    }

    // --- caps ----------------------------------------------------------------

    [Fact]
    public void Past_the_cap_the_excess_is_reported_as_wasted()
    {
        // 15 and 9 make 24 against a cap of 20, so four points bought nothing.
        var power = Bonus(Run("two", "p15", "p9"), "Power");

        Assert.True(power.OverCap);
        Assert.Equal(24, power.Total);
        Assert.Equal(20, power.Cap);
        Assert.Equal(4, power.Wasted);
    }

    [Fact]
    public void Exactly_at_the_cap_is_not_over_it()
    {
        var power = Bonus(Run("two", "p15", "p5"), "Power");

        Assert.True(power.AtCap);
        Assert.False(power.OverCap);
        Assert.Equal(0, power.Wasted);
    }

    [Fact]
    public void A_cap_that_varies_with_item_level_uses_the_items_level()
    {
        // The resist cap steps from 5 to 26 at level 40.
        Assert.Equal(5, Bonus(Run("", "two", 39, SkillCombination.HighestGem, "r3"), "Resist").Cap);
        Assert.Equal(26, Bonus(Run("", "two", 40, SkillCombination.HighestGem, "r3"), "Resist").Cap);
    }

    [Fact]
    public void A_percentage_bonus_can_breach_its_cap_too()
    {
        var report = Run("", "two", 39, SkillCombination.HighestGem, "r3", "r3");

        Assert.True(report.AnyOverCap);
        Assert.Equal(1, Bonus(report, "Resist").Wasted);
        Assert.Equal(BonusUnit.Percent, Bonus(report, "Resist").Bonus.Unit);
    }

    // --- imbue and overcharge ------------------------------------------------

    [Fact]
    public void Imbue_points_are_summed_against_the_slots_capacity()
    {
        var report = Run("two", "p5", "r3");

        Assert.Equal(5.5m, report.ImbueUsed);
        Assert.Equal(10m, report.ImbueCapacity);
        Assert.Equal(4.5m, report.ImbueLeft);
        Assert.False(report.Overcharge.IsOvercharged);
    }

    [Fact]
    public void Spending_the_capacity_exactly_is_not_an_overcharge()
    {
        // Ten of ten. The boundary belongs on the safe side.
        var report = Run("two", "p9", "p9");

        Assert.Equal(10m, report.ImbueUsed);
        Assert.Equal(0m, report.ImbueLeft);
        Assert.False(report.Overcharge.IsOvercharged);
        Assert.Equal(100, report.Overcharge.SuccessPercent);
    }

    [Fact]
    public void One_point_over_takes_the_first_overcharge_step()
    {
        var over = Run("two", "p15", "p9").Overcharge;

        Assert.Equal(1m, over.PointsOver);
        Assert.Equal(90, over.SuccessPercent);
        Assert.False(over.Impossible);
    }

    [Fact]
    public void Two_points_over_takes_the_second_step()
    {
        Assert.Equal(50, Run("two", "p15", "p15").Overcharge.SuccessPercent);
    }

    [Fact]
    public void A_fraction_of_a_point_over_still_counts_as_a_whole_point()
    {
        // 3.5 spent against a capacity of 3. Half a point over is over.
        var over = Run("solo", "r3").Overcharge;

        Assert.Equal(0.5m, over.PointsOver);
        Assert.Equal(90, over.SuccessPercent);
    }

    [Fact]
    public void Further_over_than_the_table_goes_cannot_be_made_at_all()
    {
        // 6 against a capacity of 3, and the table stops at two points over.
        var over = Run("solo", "p15").Overcharge;

        Assert.True(over.Impossible);
        Assert.Null(over.SuccessPercent);
    }

    // --- skill ---------------------------------------------------------------

    [Fact]
    public void Under_the_highest_gem_rule_the_hardest_gem_sets_the_skill()
    {
        Assert.Equal(800, Run("", "two", 50, SkillCombination.HighestGem, "p15", "p9").SkillRequired);
    }

    [Fact]
    public void Under_the_total_rule_the_gems_add_up()
    {
        Assert.Equal(1200, Run("", "two", 50, SkillCombination.TotalOfGems, "p15", "p9").SkillRequired);
    }

    // --- what does not count -------------------------------------------------

    [Fact]
    public void A_gem_that_does_not_exist_is_reported_and_adds_nothing()
    {
        var report = Run("two", "p5", "not-a-gem");

        Assert.Equal(5, Bonus(report, "Power").Total);
        Assert.Equal(2m, report.ImbueUsed);
        var rejected = Assert.Single(report.Rejected);
        Assert.Equal(1, rejected.Index);
        Assert.Contains("no such gem", rejected.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_bonus_the_slot_refuses_adds_nothing()
    {
        var report = Run("powonly", "p5", "r3");

        Assert.Equal(5, Bonus(report, "Power").Total);
        Assert.DoesNotContain(report.Bonuses, b => b.Bonus.Code == "res");
        Assert.Contains("cannot go on", Assert.Single(report.Rejected).Problem!);
    }

    [Fact]
    public void A_gem_from_another_realm_adds_nothing()
    {
        var report = Run("blue", "two", 50, SkillCombination.HighestGem, "pred", "p5");

        Assert.Equal(5, Bonus(report, "Power").Total);
        Assert.Contains("not available", Assert.Single(report.Rejected).Problem!);
    }

    [Fact]
    public void The_same_gem_counts_once_its_realm_matches()
    {
        var report = Run("red", "two", 50, SkillCombination.HighestGem, "pred", "p5");

        Assert.Equal(12, Bonus(report, "Power").Total);
        Assert.Empty(report.Rejected);
    }

    [Fact]
    public void Gem_codes_past_the_last_socket_are_dropped()
    {
        // What changing a four socket slot to a one socket slot leaves behind.
        var report = Run("solo", "p5", "p9", "p15");

        Assert.Single(report.Sockets);
        Assert.Equal(5, Bonus(report, "Power").Total);
    }

    // --- what the calculator refuses to evaluate at all -----------------------

    [Fact]
    public void An_unknown_slot_is_an_error_rather_than_an_empty_item()
    {
        var result = Tables().Resolve(new SpellcraftDesign("", "no-such-slot", 50, []));

        Assert.Null(result.Design);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void An_item_level_outside_the_table_is_refused()
    {
        Assert.NotNull(Tables().Resolve(new SpellcraftDesign("", "two", 0, [])).Error);
        Assert.NotNull(Tables().Resolve(new SpellcraftDesign("", "two", 51, [])).Error);
    }

    [Fact]
    public void A_slot_belonging_to_another_realm_is_refused()
    {
        var result = Tables().Resolve(new SpellcraftDesign("blue", "redgear", 50, []));

        Assert.Null(result.Design);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void An_unknown_realm_is_refused()
    {
        Assert.NotNull(Tables().Resolve(new SpellcraftDesign("green", "two", 50, [])).Error);
    }

    // --- the tables check themselves -----------------------------------------

    [Fact]
    public void A_gem_pointing_at_a_bonus_that_does_not_exist_fails_the_build()
    {
        var broken = new SpellcraftTables
        {
            Verified = true,
            SourceNote = "Broken on purpose.",
            SkillRule = SkillCombination.HighestGem,
            MinItemLevel = 1,
            MaxItemLevel = 50,
            Realms = [],
            Bonuses = [new("pow", "Power", BonusUnit.Points, LevelTable.Flat(20))],
            Slots = [new("two", "Two socket", 2, LevelTable.Flat(10), [], null)],
            Gems = [new("ghost", "Ghost", "Low", "nope", 1, 1m, 1, null)],
            Overcharge = [],
        };

        Assert.Throws<InvalidOperationException>(() => broken.Validated());
    }

    [Fact]
    public void A_level_table_below_its_lowest_row_uses_that_row()
    {
        var table = LevelTable.ByLevel([(20, 8m), (40, 16m)]);

        Assert.Equal(8m, table.At(1));
        Assert.Equal(8m, table.At(39));
        Assert.Equal(16m, table.At(40));
        Assert.Equal(16m, table.At(999));
    }

    [Fact]
    public void The_shipped_dataset_is_marked_unverified()
    {
        // The whole point of the marker. If this ever fails it is because somebody
        // put real numbers in without renaming the class, and the page has stopped
        // warning people.
        var placeholder = PlaceholderSpellcraftTables.Build();

        Assert.False(placeholder.Verified);
        Assert.Contains("sample", placeholder.SourceNote, StringComparison.OrdinalIgnoreCase);
    }
}

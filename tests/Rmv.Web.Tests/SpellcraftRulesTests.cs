using Rmv.Web.Tools.Spellcraft;

namespace Rmv.Web.Tests;

/// <summary>
/// Checks the rules against the specific numbers the sources state, rather than
/// against themselves.
///
/// Every assertion here is a figure somebody published: a recipe's stated skill
/// requirement, a quoted imbue capacity, a bonus a gem is documented to grant.
/// Transcribing a table by hand is exactly the sort of thing that goes wrong
/// silently, so the transcription is the thing under test.
///
/// Sources are named per test. See docs/spellcraft.md.
/// </summary>
public class SpellcraftRulesTests
{
    /// <summary>The ten stat gem values, Raw to Perfect, as the game displays them.</summary>
    private static readonly int[] StatValues = [1, 4, 7, 10, 13, 16, 19, 22, 25, 28];

    private static readonly int[] ResistValues = [1, 2, 3, 5, 7, 9, 11, 13, 15, 17];

    private static readonly int[] HitsValues = [4, 12, 20, 28, 36, 44, 52, 60, 68, 76];

    // --- gem imbue cost ------------------------------------------------------

    [Fact]
    public void Stat_gems_cost_the_odd_numbers_one_to_nineteen()
    {
        // Players describe stat cost as rising two a tier. It only does so if the
        // value fed in is the displayed bonus, which is the internal gem level
        // scaled by 1.5. That is the check: get the scaling wrong and this breaks.
        var costs = StatValues.Select(v => SpellcraftRules.GemImbueCost(BonusFamily.Stat, v));

        Assert.Equal([1, 3, 5, 7, 9, 11, 13, 15, 17, 19], costs);
    }

    [Fact]
    public void Hit_point_gems_cost_the_same_ladder_as_stats()
    {
        var costs = HitsValues.Select(v => SpellcraftRules.GemImbueCost(BonusFamily.Hits, v));

        Assert.Equal([1, 3, 5, 7, 9, 11, 13, 15, 17, 19], costs);
    }

    [Fact]
    public void Resist_and_power_gems_rise_four_a_tier_once_they_get_going()
    {
        var resist = ResistValues
            .Select(v => SpellcraftRules.GemImbueCost(BonusFamily.Resist, v))
            .ToArray();

        Assert.Equal([1, 2, 4, 8, 12, 16, 20, 24, 28, 32], resist);

        // Power uses the same formula and the same value ladder.
        var power = ResistValues.Select(v => SpellcraftRules.GemImbueCost(BonusFamily.Power, v));
        Assert.Equal(resist, power);
    }

    [Theory]
    [InlineData(4, 15)]
    [InlineData(5, 20)]
    [InlineData(7, 30)]
    [InlineData(8, 35)]
    public void Skill_gems_match_the_reference_calculators(int skill, int expected)
    {
        // The figures Zenkcraft and Allakhazam give, quoted in the Eden bug report
        // where a server had inflated them. Ours agrees with the calculators.
        Assert.Equal(expected, SpellcraftRules.GemImbueCost(BonusFamily.Skill, skill));
    }

    [Fact]
    public void Focus_gems_are_free_at_every_level()
    {
        foreach (var value in new[] { 5, 25, 50 })
        {
            Assert.Equal(1, SpellcraftRules.GemImbueCost(BonusFamily.Focus, value));
        }
    }

    // --- the item total is not a sum -----------------------------------------

    [Fact]
    public void The_largest_gem_is_paid_in_full_and_the_rest_at_half()
    {
        // 19 + 9 + 5 + 1 = 34, plus the largest again is 53, halved and floored
        // is 26. Adding them up would have said 34, which is over any item.
        Assert.Equal(26, SpellcraftRules.ItemImbueTotal([19, 9, 5, 1]));
    }

    [Fact]
    public void One_gem_alone_costs_its_own_price()
    {
        // (19 + 19) / 2 is 19. A single gem is neither discounted nor doubled.
        Assert.Equal(19, SpellcraftRules.ItemImbueTotal([19]));
    }

    [Fact]
    public void The_halving_floors_rather_than_rounds()
    {
        // (3 + 1 + 3) / 2 is 3.5, and the server floors it.
        Assert.Equal(3, SpellcraftRules.ItemImbueTotal([3, 1]));
    }

    [Fact]
    public void An_empty_item_costs_nothing()
    {
        Assert.Equal(0, SpellcraftRules.ItemImbueTotal([]));
    }

    // --- capacity ------------------------------------------------------------

    [Theory]
    [InlineData(98, 24)]
    [InlineData(99, 28)]
    [InlineData(100, 32)]
    public void A_level_51_item_holds_what_players_quote(int quality, int expected)
    {
        // The three figures repeated on every crafting forum.
        Assert.Equal(expected, SpellcraftRules.ItemCapacity(51, quality));
    }

    [Fact]
    public void The_whole_level_51_row_is_transcribed()
    {
        var row = Enumerable.Range(94, 7).Select(q => SpellcraftRules.ItemCapacity(51, q));

        Assert.Equal([10, 15, 18, 21, 24, 28, 32], row);
    }

    [Fact]
    public void Capacity_never_falls_as_level_rises()
    {
        // Catches a row transposed during transcription, which a spot check on
        // the last row would not.
        for (var quality = 94; quality <= 100; quality++)
        {
            var previous = 0;
            for (var level = 1; level <= 51; level++)
            {
                var here = SpellcraftRules.ItemCapacity(level, quality);
                Assert.True(here >= previous, $"level {level} quality {quality} dropped to {here}");
                previous = here;
            }
        }
    }

    [Fact]
    public void Capacity_never_falls_as_quality_rises()
    {
        for (var level = 1; level <= 51; level++)
        {
            var previous = 0;
            for (var quality = 94; quality <= 100; quality++)
            {
                var here = SpellcraftRules.ItemCapacity(level, quality);
                Assert.True(here >= previous, $"level {level} quality {quality} dropped to {here}");
                previous = here;
            }
        }
    }

    [Fact]
    public void An_item_past_the_table_is_treated_as_level_51()
    {
        Assert.Equal(32, SpellcraftRules.ItemCapacity(60, 100));
        Assert.Equal(0, SpellcraftRules.ItemCapacity(0, 100));
    }

    // --- skill ---------------------------------------------------------------

    [Fact]
    public void Imbuing_needs_twenty_skill_a_point()
    {
        // The server refuses when the imbue total exceeds skill over twenty, so a
        // 32 point item needs 640.
        Assert.Equal(640, SpellcraftRules.SkillToImbue(32));
    }

    [Theory]
    [InlineData(1, 18, 18)]     // Raw Fiery Essence Jewel, recipe says 18
    [InlineData(10, 18, 918)]   // Perfect Fiery Essence Jewel, recipe says 918
    [InlineData(1, 1, 1)]       // Raw Icy Essence Jewel, recipe says 1
    [InlineData(2, 1, 101)]     // Uncut Icy Essence Jewel, recipe says 101
    [InlineData(2, 18, 118)]    // Uncut Fiery Essence Jewel, recipe says 118
    public void Cutting_a_gem_needs_what_its_recipe_states(int tier, int offset, int expected)
    {
        Assert.Equal(expected, SpellcraftRules.SkillToCutGem(tier, offset));
    }

    // --- materials -----------------------------------------------------------

    [Fact]
    public void Tempers_and_reagents_match_the_published_recipes()
    {
        // Raw Fiery Essence Jewel: 1 temper, 1 reagent.
        Assert.Equal(1, SpellcraftRules.TempersFor(1));
        Assert.Equal(1, SpellcraftRules.ReagentsFor(1));

        // Imperfect, the fifth tier: 17 tempers, 5 reagents.
        Assert.Equal(17, SpellcraftRules.TempersFor(5));
        Assert.Equal(5, SpellcraftRules.ReagentsFor(5));

        // Perfect Fiery Essence Jewel: 37 tempers, 10 reagents.
        Assert.Equal(37, SpellcraftRules.TempersFor(10));
        Assert.Equal(10, SpellcraftRules.ReagentsFor(10));
    }

    // --- overcharge ----------------------------------------------------------

    [Fact]
    public void Within_capacity_nothing_can_go_wrong()
    {
        Assert.Equal(100, SpellcraftRules.OverchargeChance(0, 100, 0));
        Assert.Equal(100, SpellcraftRules.OverchargeChance(-3, 94, 0));
    }

    [Fact]
    public void Past_five_points_over_the_item_cannot_be_made()
    {
        Assert.Equal(0, SpellcraftRules.OverchargeChance(6, 100, 1000));
    }

    [Fact]
    public void A_maxed_crafter_on_a_perfect_item_is_certain_up_to_three_over()
    {
        // 34 + 26 quality + 100 skill, less the 30 that three over costs, with no
        // fudge penalty at skill 1000.
        Assert.Equal(100, SpellcraftRules.OverchargeChance(3, 100, 1000));
    }

    [Fact]
    public void Quality_and_skill_both_move_the_chance_the_right_way()
    {
        var poor = SpellcraftRules.OverchargeChance(5, 94, 1000);
        var good = SpellcraftRules.OverchargeChance(5, 100, 1000);
        Assert.True(good > poor, $"quality did not help: {poor} then {good}");

        var unskilled = SpellcraftRules.OverchargeChance(5, 100, 500);
        Assert.True(good > unskilled, $"skill did not help: {unskilled} then {good}");
    }

    [Fact]
    public void The_chance_only_falls_as_the_overcharge_deepens()
    {
        var previous = 101;
        for (var over = 1; over <= SpellcraftRules.MaxOvercharge; over++)
        {
            var here = SpellcraftRules.OverchargeChance(over, 99, 900);
            Assert.True(here <= previous, $"{over} over rose to {here}");
            previous = here;
        }
    }

    // --- character caps ------------------------------------------------------

    [Fact]
    public void A_level_fifty_character_caps_where_every_guide_says()
    {
        Assert.Equal(75, SpellcraftRules.CharacterCap(BonusFamily.Stat, 50));
        Assert.Equal(26, SpellcraftRules.CharacterCap(BonusFamily.Resist, 50));
        Assert.Equal(26, SpellcraftRules.CharacterCap(BonusFamily.Power, 50));
        Assert.Equal(200, SpellcraftRules.CharacterCap(BonusFamily.Hits, 50));
        Assert.Equal(11, SpellcraftRules.CharacterCap(BonusFamily.Skill, 50));
    }
}

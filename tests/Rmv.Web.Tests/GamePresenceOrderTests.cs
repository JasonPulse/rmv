using Rmv.Web.Data;

namespace Rmv.Web.Tests;

/// <summary>
/// Newest first on the history page.
///
/// The order comes out of the Period text, because Period is already what an admin
/// types and every value in it is a year range. A date column would have meant
/// re-entering twenty years of history to sort a page.
/// </summary>
public class GamePresenceOrderTests
{
    private static GamePresence G(string game, string? period, bool active = false, int sort = 0) =>
        new() { Game = game, Period = period, IsActive = active, SortOrder = sort };

    [Theory]
    [InlineData("2001-2005", 2001, 2005)]
    [InlineData("2013-2020", 2013, 2020)]
    // A single year is both ends of the range.
    [InlineData("2009", 2009, 2009)]
    // Whatever separator someone reaches for.
    [InlineData("2001 to 2005", 2001, 2005)]
    [InlineData("2001 / 2005", 2001, 2005)]
    public void Reads_the_years_out_of_a_period(string period, int start, int end)
    {
        var g = G("x", period);

        Assert.Equal(start, g.StartYear);
        Assert.Equal(end, g.EndYear);
    }

    [Theory]
    [InlineData("2025-Present")]
    [InlineData("2025-present")]
    [InlineData("2025 - Now")]
    [InlineData("2025-")]
    public void Ongoing_beats_every_year(string period)
    {
        var g = G("x", period);

        Assert.Equal(2025, g.StartYear);
        Assert.Equal(int.MaxValue, g.EndYear);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ages ago")]
    [InlineData("the 90s")]
    public void An_unreadable_period_sorts_last_rather_than_first(string? period)
    {
        // Zero, not MaxValue. A typo must not silently jump a game to the top.
        Assert.Equal(0, G("x", period).EndYear);
    }

    [Fact]
    public void Four_digits_inside_a_longer_number_are_not_a_year()
    {
        Assert.Equal(0, G("x", "patch 123456").EndYear);
    }

    [Fact]
    public void The_real_history_comes_out_newest_first()
    {
        // The live rows, with the periods as they are actually written. The old
        // order was SortOrder then alphabetical, which put Aion above Final Fantasy
        // XIV and Uthgard below World of Warcraft.
        var games = new[]
        {
            G("Blackthorn DAoC", "2026-Present", active: true),
            G("Final Fantasy XI Mhaue Docks", "2025-Present", active: true),
            G("Aion", "2008-2009"),
            G("Dark Age of Camelot", "2001-2005"),
            G("Eve Online", "2003-2004"),
            G("Final Fantasy XI Retail", "2002-2004"),
            G("Final Fantasy XIV ARR", "2013-2020"),
            G("Uthgard DAoC", "2011-2015"),
            G("World of Warcraft", "2004-2012"),
        };

        var order = games.OrderBy(g => g.NewestFirst).Select(g => g.Game).ToArray();

        Assert.Equal(
            [
                "Blackthorn DAoC",
                "Final Fantasy XI Mhaue Docks",
                "Final Fantasy XIV ARR",
                "Uthgard DAoC",
                "World of Warcraft",
                "Aion",
                "Dark Age of Camelot",
                // Both ended 2004. Eve started 2003 and Retail 2002, so the longer
                // presence reads as the older one and goes below.
                "Eve Online",
                "Final Fantasy XI Retail",
            ],
            order);
    }

    [Fact]
    public void An_active_game_leads_even_with_no_period_filled_in()
    {
        var games = new[] { G("Old", "2013-2020"), G("Current", null, active: true) };

        Assert.Equal(["Current", "Old"], games.OrderBy(g => g.NewestFirst).Select(g => g.Game));
    }

    [Fact]
    public void Two_that_ended_together_are_split_by_when_they_started()
    {
        // Eve ran 2003-2004 and FFXI Retail 2002-2004. The longer presence reads
        // as the older one, so it goes below.
        var games = new[] { G("Eve", "2003-2004"), G("Retail", "2002-2004") };

        Assert.Equal(["Eve", "Retail"], games.OrderBy(g => g.NewestFirst).Select(g => g.Game));
    }

    [Fact]
    public void Sort_order_still_breaks_a_tie_an_admin_can_control()
    {
        var games = new[]
        {
            G("Second", "2010-2012", sort: 2),
            G("First", "2010-2012", sort: 1),
        };

        Assert.Equal(["First", "Second"], games.OrderBy(g => g.NewestFirst).Select(g => g.Game));
    }
}

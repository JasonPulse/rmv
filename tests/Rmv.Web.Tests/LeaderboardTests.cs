using Rmv.Web.Data;
using Rmv.Web.Herald;

namespace Rmv.Web.Tests;

/// <summary>
/// Ordering, ties and exclusions on a board. Pure, so no database and no herald.
/// </summary>
public class LeaderboardTests
{
    private static Character C(string name, long? score = null, int? level = null) =>
        new() { Name = name, Score = score, Level = level, Source = CharacterSource.Herald };

    [Fact]
    public void Highest_first()
    {
        var rows = Leaderboard.Rank([C("Low", 10), C("High", 900), C("Mid", 400)], RankBy.Score);

        Assert.Equal(["High", "Mid", "Low"], rows.Select(r => r.Character.Name));
        Assert.Equal([1, 2, 3], rows.Select(r => r.Position));
        Assert.Equal([900, 400, 10], rows.Select(r => r.Value));
    }

    [Fact]
    public void A_tie_shares_a_position_and_the_next_value_skips_one()
    {
        // Two firsts are followed by a third. A second would claim someone came
        // second when nobody did.
        var rows = Leaderboard.Rank([C("A", 500), C("B", 500), C("C", 100)], RankBy.Score);

        Assert.Equal([1, 1, 3], rows.Select(r => r.Position));
    }

    [Fact]
    public void A_tie_is_broken_by_name_so_the_order_does_not_wander()
    {
        // Same numbers, different input order, same output. Otherwise the board
        // reshuffles itself between page loads for no reason anyone can see.
        var first = Leaderboard.Rank([C("zeta", 500), C("Alpha", 500)], RankBy.Score);
        var second = Leaderboard.Rank([C("Alpha", 500), C("zeta", 500)], RankBy.Score);

        Assert.Equal(["Alpha", "zeta"], first.Select(r => r.Character.Name));
        Assert.Equal(first.Select(r => r.Character.Name), second.Select(r => r.Character.Name));
    }

    [Fact]
    public void Zero_and_null_are_absent_rather_than_last()
    {
        // A character the herald has not answered for yet, or one on a herald that
        // does not publish this measure. Listing them at the bottom reads as a
        // result, and it is not one.
        var rows = Leaderboard.Rank(
            [C("Real", 300), C("Zero", 0), C("Null", null)], RankBy.Score);

        Assert.Equal(["Real"], rows.Select(r => r.Character.Name));
    }

    [Fact]
    public void Ranking_by_level_reads_the_level_not_the_score()
    {
        // The Lodestone publishes no cumulative number, so its board ranks on level
        // and must ignore a Score that happens to be set.
        var rows = Leaderboard.Rank(
            [C("Sixty", score: 1, level: 60), C("Ninety", score: 9999, level: 90)],
            RankBy.Level);

        Assert.Equal(["Ninety", "Sixty"], rows.Select(r => r.Character.Name));
        Assert.Equal([90, 60], rows.Select(r => r.Value));
    }

    [Fact]
    public void Nothing_to_rank_is_an_empty_board_not_an_error()
    {
        Assert.Empty(Leaderboard.Rank([], RankBy.Score));
        Assert.Empty(Leaderboard.Rank([C("Nobody")], RankBy.Score));
    }

    [Fact]
    public void Every_adapter_ranks_on_something_it_actually_fills_in()
    {
        // The trap this guards: a board ranking on a column its herald never
        // populates renders as an empty table for a game that plainly has
        // characters. Blackthorn and HeraldXI both set Score; the Lodestone sets
        // neither Score nor Kills, so it has to be Level.
        var fetcher = new HeraldFetcher(
            new HttpClient(), Microsoft.Extensions.Logging.Abstractions.NullLogger<HeraldFetcher>.Instance);

        Assert.Equal(RankBy.Score, new BlackthornHeraldAdapter(fetcher).Metric.By);
        Assert.Equal(RankBy.Score, new HeraldXiAdapter(fetcher).Metric.By);
        Assert.Equal(RankBy.Level, new LodestoneAdapter(fetcher).Metric.By);

        foreach (IHeraldAdapter adapter in new IHeraldAdapter[]
                 {
                     new BlackthornHeraldAdapter(fetcher),
                     new HeraldXiAdapter(fetcher),
                     new LodestoneAdapter(fetcher),
                 })
        {
            Assert.False(string.IsNullOrWhiteSpace(adapter.Metric.Label), adapter.Key);
        }
    }
}

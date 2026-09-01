using Microsoft.Extensions.Logging.Abstractions;
using Rmv.Web.Data;
using Rmv.Web.Herald;
using Rmv.Web.Signature;

namespace Rmv.Web.Tests;

/// <summary>
/// Each herald's own stats, and the question he asked: can one signature carry
/// tokens from several heralds at once.
///
/// It can, and the mechanism is per-element binding rather than a namespace. A line
/// bound to a DAoC character resolves %Relics%; the same token on a line bound to an
/// FFXI character resolves to nothing, because it is a real stat that does not apply
/// rather than a typo. Both lines and a totals line live on the same canvas.
/// </summary>
public class HeraldStatTokenTests
{
    private static HeraldFetcher Fetcher() =>
        new(new HttpClient(new StubImageHandler()), NullLogger<HeraldFetcher>.Instance);

    private static IReadOnlyList<IHeraldAdapter> Adapters() =>
    [
        new BlackthornHeraldAdapter(Fetcher()),
        new HeraldXiAdapter(Fetcher()),
        new LodestoneAdapter(Fetcher()),
        new WowArmoryAdapter(Fetcher()),
    ];

    /// <summary>A character on a herald, carrying that herald's stats.</summary>
    private static Character On(string game, params (string Key, string Value)[] stats) => new()
    {
        Id = Math.Abs(game.GetHashCode() % 1000) + 1,
        Name = "Somebody",
        Level = 50,
        AddedAt = DateTimeOffset.UtcNow,
        GamePresenceId = 1,
        Game = new GamePresence { Id = 1, Game = game },
        Stats = HeraldStats.Serialise(
            stats.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase)),
    };

    private static SignatureSubject Subject(IReadOnlyList<Character> roster, int? characterId) =>
        SignatureData.Subject(new Member { DisplayName = "Property" }, roster, characterId);

    // --- what each herald declares -------------------------------------------

    [Fact]
    public void Every_herald_declares_what_it_publishes()
    {
        // The DAoC page has a stats matrix, the FFXI API has thirty fields, the
        // Armory has an item level. The Lodestone publishes nothing a shared field
        // does not already carry, and declaring nothing is a legitimate answer.
        var declared = Adapters().ToDictionary(a => a.Key, a => a.Stats);

        Assert.NotEmpty(declared["blackthorn"]);
        Assert.NotEmpty(declared["heraldxi"]);
        Assert.NotEmpty(declared["armory"]);

        // And the one the 2001 generator had that this project had dropped.
        Assert.Contains(declared["blackthorn"], s => s.Key == "LastWeek");
    }

    [Fact]
    public void No_herald_can_shadow_a_token_every_character_has()
    {
        // Resolution checks the shared tokens first, so a herald declaring %Name%
        // would be declaring something that never resolves to its value. Better to
        // fail here than to leave somebody wondering.
        foreach (var adapter in Adapters())
        {
            foreach (var stat in adapter.Stats)
            {
                Assert.Null(SignatureTokens.Find(stat.Key));
            }
        }
    }

    [Fact]
    public void No_two_heralds_disagree_about_a_key()
    {
        // They may share one: two games with an item level would both be %ItemLevel%
        // and that reads fine, because the value comes from the line's own character.
        // What must not happen is two heralds using one key for different things, so
        // this prints them rather than asserting none, and the labels have to match.
        var byKey = Adapters()
            .SelectMany(a => a.Stats.Select(s => (a.DisplayName, s)))
            .GroupBy(x => x.s.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in byKey)
        {
            var labels = group.Select(x => x.s.Label).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            Assert.True(labels.Count == 1,
                $"{group.Key} means different things on "
                + string.Join(" and ", group.Select(x => $"{x.DisplayName} ({x.s.Label})")));
        }
    }

    [Fact]
    public void Every_declared_key_is_usable_as_a_token()
    {
        foreach (var adapter in Adapters())
        {
            foreach (var stat in adapter.Stats)
            {
                // A key with a percent or a space in it could not be written as a
                // token at all.
                Assert.Matches("^[A-Za-z][A-Za-z0-9]*$", stat.Key);
                Assert.False(string.IsNullOrWhiteSpace(stat.Label));
                Assert.False(string.IsNullOrWhiteSpace(stat.Example));
            }
        }
    }

    // --- resolving them ------------------------------------------------------

    [Fact]
    public void A_line_draws_its_own_characters_herald_stats()
    {
        var daoc = On("Dark Age of Camelot", ("LastWeek", "11,792"), ("Relics", "7"));

        var line = SignatureTokens.Resolve(
            "%LastWeek% last week, %Relics% relics", Subject([daoc], daoc.Id));

        Assert.Equal("11,792 last week, 7 relics", line);
    }

    [Fact]
    public void One_signature_carries_several_heralds_at_once()
    {
        // The question, answered. Three lines about three games and a fourth about
        // all of them, resolved the way the renderer resolves them: one subject per
        // element, bound to that element's character.
        var daoc = On("Dark Age of Camelot", ("LastWeek", "11,792"), ("Relics", "7"));
        var ffxi = On("Final Fantasy XI", ("Playtime", "15 days"), ("MasterLevel", "20"));
        var wow = On("World of Warcraft", ("ItemLevel", "146"), ("Faction", "Alliance"));

        var roster = new[] { daoc, ffxi, wow };

        var lines = new[]
        {
            (daoc.Id, "DAoC: %Relics% relics, %LastWeek% last week"),
            (ffxi.Id, "FFXI: %Playtime% played, master %MasterLevel%"),
            (wow.Id, "WoW: item level %ItemLevel%, %Faction%"),
            ((int?)null, "%User% across %AllGames% games"),
        };

        var drawn = lines
            .Select(l => SignatureTokens.Resolve(l.Item2, Subject(roster, l.Item1)))
            .ToList();

        Assert.Equal("DAoC: 7 relics, 11,792 last week", drawn[0]);
        Assert.Equal("FFXI: 15 days played, master 20", drawn[1]);
        Assert.Equal("WoW: item level 146, Alliance", drawn[2]);
        Assert.Equal("Property across 1 games", drawn[3]);
    }

    [Fact]
    public void A_stat_from_another_herald_draws_nothing_rather_than_its_own_name()
    {
        // %Relics% on an FFXI line is a real token for a game this character is not
        // on. Leaving it as typed would put "%Relics%" in somebody's signature; the
        // gap is the honest answer.
        var ffxi = On("Final Fantasy XI", ("Playtime", "15 days"));

        var line = SignatureTokens.Resolve("[%Relics%][%Playtime%]", Subject([ffxi], ffxi.Id));

        Assert.Equal("[][15 days]", line);
    }

    [Fact]
    public void A_mistyped_stat_is_still_left_where_it_can_be_seen()
    {
        // The distinction that makes the rule above safe: a name no herald declares
        // is a typo, and a typo has to be visible.
        var daoc = On("Dark Age of Camelot", ("Relics", "7"));

        var line = SignatureTokens.Resolve("%Rellics% and %Relics%", Subject([daoc], daoc.Id));

        Assert.Equal("%Rellics% and 7", line);
    }

    [Fact]
    public void A_hand_typed_character_has_no_herald_stats_and_that_is_fine()
    {
        var manual = new Character
        {
            Id = 9, Name = "Typed", Level = 20, AddedAt = DateTimeOffset.UtcNow,
            GamePresenceId = 2, Source = CharacterSource.Manual,
        };

        Assert.Empty(SignatureData.Of(manual).Stats);
        Assert.Equal("", SignatureTokens.Resolve("%Relics%", Subject([manual], manual.Id)));
    }

    [Fact]
    public void An_unbound_line_draws_no_herald_stat()
    {
        var daoc = On("Dark Age of Camelot", ("Relics", "7"));

        Assert.Equal("", SignatureTokens.Resolve("%Relics%", Subject([daoc], null)));
    }

    // --- storing them --------------------------------------------------------

    [Fact]
    public void Stats_survive_the_round_trip_through_the_column()
    {
        var stats = new Dictionary<string, string>
        {
            ["LastWeek"] = "11,792",
            ["Ratio"] = "2.92",
            ["Zone"] = "Eastern Adoulin",
        };

        var read = HeraldStats.Read(HeraldStats.Serialise(stats));

        Assert.Equal(3, read.Count);
        Assert.Equal("11,792", read["LastWeek"]);
        // Case-insensitive, because a member typing %lastweek% means the same thing.
        Assert.Equal("11,792", read["lastweek"]);
    }

    [Fact]
    public void Nothing_to_say_is_stored_as_nothing()
    {
        Assert.Null(HeraldStats.Serialise(null));
        Assert.Null(HeraldStats.Serialise(new Dictionary<string, string>()));
        // Blank values are not worth a column.
        Assert.Null(HeraldStats.Serialise(new Dictionary<string, string> { ["A"] = "  " }));
    }

    [Fact]
    public void A_herald_cannot_push_an_unbounded_amount_into_a_row()
    {
        var flood = Enumerable.Range(0, 500)
            .ToDictionary(i => $"Stat{i}", i => new string('x', 500));

        var read = HeraldStats.Read(HeraldStats.Serialise(flood));

        Assert.Equal(HeraldStats.MaxStats, read.Count);
        Assert.All(read.Values, v => Assert.True(v.Length <= 60, $"{v.Length} characters"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("not json")]
    [InlineData("{\"unclosed\": ")]
    [InlineData("[1,2,3]")]
    public void A_column_that_will_not_parse_reads_as_nothing(string json)
    {
        // A row written by an older shape or edited by hand. A signature with one
        // empty token beats a signature that will not render.
        Assert.Empty(HeraldStats.Read(json));
    }

    [Fact]
    public void The_resolver_knows_every_declared_key_without_a_registry()
    {
        // Resolve is static and pure, so it cannot ask the registry which stats
        // exist. Adapters declare into a shared set as they are constructed, and
        // every adapter is built at startup.
        _ = Adapters();

        Assert.True(HeraldStatTokens.IsKnown("LastWeek"));
        Assert.True(HeraldStatTokens.IsKnown("lastweek"));
        Assert.True(HeraldStatTokens.IsKnown("ItemLevel"));
        Assert.True(HeraldStatTokens.IsKnown("Playtime"));
        Assert.False(HeraldStatTokens.IsKnown("Rellics"));
    }

    [Fact]
    public void The_palette_groups_them_by_herald()
    {
        var groups = new HeraldStatTokens(new HeraldRegistry(Adapters())).All;

        // One group per herald that has any, named so somebody can tell which game a
        // stat belongs to.
        Assert.Contains(groups, g => g.Herald.Contains("Blackthorn", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(groups, g => g.Stats.Any(s => s.Key == "ItemLevel"));
        Assert.All(groups, g => Assert.NotEmpty(g.Stats));
        Assert.All(groups, g => Assert.False(string.IsNullOrWhiteSpace(g.Herald)));
    }
}

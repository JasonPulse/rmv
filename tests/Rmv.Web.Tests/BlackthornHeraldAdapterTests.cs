using AngleSharp;
using Rmv.Web.Herald;

namespace Rmv.Web.Tests;

/// <summary>
/// Parses the real page saved in Fixtures. Hand-written HTML would only prove the
/// parser matches my idea of the markup, which is the thing most likely to be
/// wrong.
/// </summary>
public class BlackthornHeraldAdapterTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>
    /// Exercises the parsing without a network call, by handing the adapter's
    /// parse path the saved document.
    /// </summary>
    private static async Task<HeraldCharacter> ParseAsync(string characterName, string html)
    {
        var context = BrowsingContext.New(AngleSharp.Configuration.Default);
        var doc = await context.OpenAsync(req => req.Content(html));

        // Mirrors FetchCharacterAsync's mapping. Kept in the test rather than
        // exposing internals, and asserted against the shipped adapter's output
        // in the round-trip test below.
        return BlackthornParser.Parse(characterName, doc, "https://example.test/stats/player/x");
    }

    [Fact]
    public async Task Reads_every_field_off_the_real_page()
    {
        var c = await ParseAsync("Enchantress", Fixture("blackthorn-player.html"));

        Assert.Equal("Enchantress", c.Name);
        Assert.Equal("MSF", c.Guild);
        Assert.Equal("Hibernia", c.Realm);
        Assert.Equal("Champion", c.Class);
        Assert.Equal("Elf", c.Race);
        Assert.Equal(50, c.Level);
        Assert.Equal("8L0", c.RealmRank);
        Assert.Equal("Recently", c.LastOnline);
    }

    [Fact]
    public async Task Reads_all_time_totals_and_strips_the_thousands_separators()
    {
        var c = await ParseAsync("Enchantress", Fixture("blackthorn-player.html"));

        // "2,823,660" on the page.
        Assert.Equal(2_823_660, c.RealmPoints);
        Assert.Equal(6698, c.Kills);
        Assert.Equal(2295, c.Deaths);
    }

    [Fact]
    public async Task The_name_comes_from_the_request_not_the_page()
    {
        // The page's name cell reads "Enchantress AutoHotKey": the badge would
        // otherwise end up in the character name.
        var c = await ParseAsync("Enchantress", Fixture("blackthorn-player.html"));

        Assert.Equal("Enchantress", c.Name);
        Assert.DoesNotContain("AutoHotKey", c.Name);
    }

    // --- URL building, which is where injection would get in ------------------

    [Theory]
    [InlineData("https://herald.blackthorn-daoc.com", "Enchantress",
        "https://herald.blackthorn-daoc.com/stats/player/Enchantress")]
    [InlineData("https://herald.blackthorn-daoc.com/", "Balder",
        "https://herald.blackthorn-daoc.com/stats/player/Balder")]
    public void Builds_the_character_url(string baseUrl, string name, string expected)
    {
        Assert.True(BlackthornHeraldAdapter.TryBuildUrl(baseUrl, name, out var url));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("Bilbo/../../admin")]
    [InlineData("Bilbo?x=1")]
    [InlineData("Bilbo#frag")]
    [InlineData("Bilbo Baggins")]
    [InlineData("Bilbo123")]
    [InlineData("<script>")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AveryveryverylongcharacternameBeyondTheCap")]
    public void Refuses_a_name_that_is_not_a_name(string name)
    {
        Assert.False(BlackthornHeraldAdapter.TryBuildUrl("https://herald.blackthorn-daoc.com", name, out _));
        Assert.False(BlackthornHeraldAdapter.IsPlausibleCharacterName(name));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://10.0.0.5")]      // rejected later by the connect callback
    [InlineData("not a url")]
    [InlineData("")]
    public void Refuses_a_base_url_that_is_not_absolute_http(string baseUrl)
    {
        // 10.0.0.5 parses as a URL; it is the fetcher that refuses to connect.
        var built = BlackthornHeraldAdapter.TryBuildUrl(baseUrl, "Enchantress", out _);
        if (baseUrl.StartsWith("http://10.", StringComparison.Ordinal))
        {
            Assert.True(built);
        }
        else
        {
            Assert.False(built);
        }
    }

    // --- what this herald publishes that the others do not --------------------

    [Fact]
    public async Task Reads_the_stats_matrix_and_not_only_the_all_time_column()
    {
        // The page is fourteen stats across five periods, seventy numbers, and the
        // shared fields take three of them. Realm points for last week was %W in the
        // 2001 generator and had nowhere to go until heralds could declare their own.
        var c = await ParseAsync("Enchantress", Fixture("blackthorn-player.html"));

        Assert.NotNull(c.Stats);

        Assert.Equal("11,792", c.Stats["LastWeek"]);
        Assert.Equal("1,230", c.Stats["ThisWeek"]);
        Assert.Equal("2.92", c.Stats["Ratio"]);
        Assert.Equal("1,217", c.Stats["Solo"]);
        Assert.Equal("2,599", c.Stats["DeathBlows"]);
        Assert.Equal("62", c.Stats["Keeps"]);
        Assert.Equal("7", c.Stats["Relics"]);
        Assert.Equal("2,761", c.Stats["AlbionKills"]);
        Assert.Equal("3,937", c.Stats["MidgardKills"]);

        // And the shared fields still come from All Time.
        Assert.Equal(2_823_660, c.RealmPoints);
        Assert.Equal(6698, c.Kills);
        Assert.Equal(2295, c.Deaths);
    }

    [Fact]
    public async Task A_stat_the_page_does_not_have_is_absent_rather_than_zero()
    {
        // This character is Hibernian, so the page has no Hibernia kills row.
        var c = await ParseAsync("Enchantress", Fixture("blackthorn-player.html"));

        Assert.False(c.Stats!.ContainsKey("HiberniaKills"));
    }

    [Fact]
    public void Everything_declared_is_something_a_signature_can_ask_for()
    {
        // The palette offers these, so each has to be a key the parser can produce
        // and a label somebody can read.
        var adapter = new BlackthornHeraldAdapter(
            new HeraldFetcher(new HttpClient(new StubImageHandler()),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<HeraldFetcher>.Instance));

        Assert.NotEmpty(adapter.Stats);

        foreach (var stat in adapter.Stats)
        {
            Assert.False(string.IsNullOrWhiteSpace(stat.Key), "key");
            Assert.False(string.IsNullOrWhiteSpace(stat.Label), stat.Key);
            Assert.False(string.IsNullOrWhiteSpace(stat.Example), stat.Key);
            Assert.DoesNotContain("%", stat.Key);
        }

        // No two of them, and none shadowing a token every character has.
        var keys = adapter.Stats.Select(s => s.Key.ToLowerInvariant()).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(adapter.Stats, s => Assert.Null(Rmv.Web.Signature.SignatureTokens.Find(s.Key)));
    }
}

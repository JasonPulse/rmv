using System.Text.Json;
using Rmv.Web.Herald;

namespace Rmv.Web.Tests;

/// <summary>
/// Mapped from a record saved off the live API, so the field names are the real
/// ones rather than what I assumed they were called.
/// </summary>
public class HeraldXiAdapterTests
{
    private static HeraldXiAdapter.XiCharacter Load()
    {
        var json = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "heraldxi-character.json"));

        return JsonSerializer.Deserialize<HeraldXiAdapter.XiCharacter>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    public void Maps_the_real_api_record()
    {
        var c = HeraldXiAdapter.Map(Load(), "https://example.test/api/v1/characters/Arwen");

        Assert.Equal("Arwen", c.Name);
        Assert.Equal("Windurst", c.Realm);
        Assert.Equal("Elvaan", c.Race);
        Assert.Equal("New Adventurer", c.RealmRank);
        Assert.Equal(1, c.Level);
    }

    [Fact]
    public void Hides_the_empty_subjob_rather_than_printing_it()
    {
        // The API writes "---" for no subjob. Rendering that in a signature would
        // look like a bug.
        var c = HeraldXiAdapter.Map(Load(), "u");

        Assert.Equal("MNK 1", c.Class);
        Assert.DoesNotContain("---", c.Class);
    }

    [Fact]
    public void Shows_both_jobs_when_a_subjob_is_set()
    {
        var dto = Load();
        dto.SubJob = "WHM";
        dto.SubJobLevel = 37;
        dto.MainJob = "MNK";
        dto.MainJobLevel = 75;

        Assert.Equal("MNK 75 / WHM 37", HeraldXiAdapter.Map(dto, "u").Class);
    }

    [Fact]
    public void Zeroes_are_reported_as_absent_not_as_zero()
    {
        // This character has no kills or deaths. A signature should omit the row
        // rather than claim a real zero.
        var c = HeraldXiAdapter.Map(Load(), "u");

        Assert.Null(c.Kills);
        Assert.Null(c.Deaths);
    }

    [Fact]
    public void Total_job_levels_stands_in_for_realm_points()
    {
        // FFXI has no realm points; total job levels is what the herald's own
        // leaderboards rank on.
        var c = HeraldXiAdapter.Map(Load(), "u");

        Assert.Equal(6, c.RealmPoints);
    }

    [Fact]
    public void Online_beats_a_logout_timestamp()
    {
        var dto = Load();
        dto.Online = true;

        Assert.Equal("Online now", HeraldXiAdapter.Map(dto, "u").LastOnline);
    }

    [Fact]
    public void Falls_back_to_the_logout_date_when_offline()
    {
        var c = HeraldXiAdapter.Map(Load(), "u");

        Assert.Equal("2026-02-15", c.LastOnline);
    }

    [Theory]
    [InlineData("https://heraldxi.network-gnomes.com", "Arwen",
        "https://heraldxi.network-gnomes.com/api/v1/characters/Arwen")]
    [InlineData("https://heraldxi.network-gnomes.com/", "Dengra",
        "https://heraldxi.network-gnomes.com/api/v1/characters/Dengra")]
    public void Builds_the_api_url(string baseUrl, string name, string expected)
    {
        Assert.True(HeraldXiAdapter.TryBuildUrl(baseUrl, name, out var url));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("../../admin")]
    [InlineData("Arwen/../x")]
    [InlineData("Arwen?x=1")]
    [InlineData("Arwen Smith")]
    [InlineData("Arwen1")]
    [InlineData("Averyveryverylongname")]
    [InlineData("")]
    public void Refuses_a_name_that_is_not_a_name(string name)
    {
        Assert.False(HeraldXiAdapter.IsPlausibleCharacterName(name));
        Assert.False(HeraldXiAdapter.TryBuildUrl("https://heraldxi.network-gnomes.com", name, out _));
    }
}

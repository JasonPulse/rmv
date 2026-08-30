using System.Text.Json;
using Rmv.Web.Herald;

namespace Rmv.Web.Tests;

/// <summary>
/// Mapped from a record saved off the live API, so the field names are the real
/// ones rather than what I assumed they were called.
/// </summary>
public class HeraldXiAdapterTests
{
    private const string Base = "https://heraldxi.example.test";

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
        var c = HeraldXiAdapter.Map(Load(), "https://example.test/api/v1/characters/Arwen", Base);

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
        var c = HeraldXiAdapter.Map(Load(), "u", Base);

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

        Assert.Equal("MNK 75 / WHM 37", HeraldXiAdapter.Map(dto, "u", Base).Class);
    }

    [Fact]
    public void Zeroes_are_reported_as_absent_not_as_zero()
    {
        // This character has no kills or deaths. A signature should omit the row
        // rather than claim a real zero.
        var c = HeraldXiAdapter.Map(Load(), "u", Base);

        Assert.Null(c.Kills);
        Assert.Null(c.Deaths);
    }

    [Fact]
    public void Total_job_levels_stands_in_for_realm_points()
    {
        // FFXI has no realm points; total job levels is what the herald's own
        // leaderboards rank on.
        var c = HeraldXiAdapter.Map(Load(), "u", Base);

        Assert.Equal(6, c.RealmPoints);
    }

    [Fact]
    public void Online_beats_a_logout_timestamp()
    {
        var dto = Load();
        dto.Online = true;

        Assert.Equal("Online now", HeraldXiAdapter.Map(dto, "u", Base).LastOnline);
    }

    [Fact]
    public void Falls_back_to_the_logout_date_when_offline()
    {
        var c = HeraldXiAdapter.Map(Load(), "u", Base);

        Assert.Equal("2026-02-15", c.LastOnline);
    }

    [Theory]
    [InlineData("https://heraldxi.network-gnomes.com", "Arwen",
        "https://heraldxi.network-gnomes.com/api/v1/characters/Arwen")]
    [InlineData("https://heraldxi.network-gnomes.com/", "Dengra",
        "https://heraldxi.network-gnomes.com/api/v1/characters/Dengra")]
    public void Builds_the_api_url(string baseUrl, string name, string expected)
    {
        Assert.True(HeraldXiAdapter.TryApiUrl(baseUrl, name, out var url));
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
        Assert.False(HeraldXiAdapter.TryApiUrl("https://heraldxi.network-gnomes.com", name, out _));
    }

    [Fact]
    public void Maps_the_portrait_to_the_herald_route_and_the_appearance_hash()
    {
        // The route is not in the API's own endpoint list; it is what the herald's
        // player pages use, and it asks for the appearance hash.
        //
        // The version is wider than that hash on purpose. This test used to assert
        // they were the same thing, which is what the herald's notes say to do and
        // is wrong: it serves different renders under one hash. See MapPortrait.
        var portrait = HeraldXiAdapter.MapPortrait(Load(), Base);

        Assert.NotNull(portrait);
        Assert.Equal("https://heraldxi.example.test/portraits/3.png?v=a57c46727615", portrait.Url);
        Assert.Equal("a57c46727615|body=8,hands=8,legs=8,feet=8,main=21", portrait.Version);
    }

    [Fact]
    public void A_trailing_slash_on_the_base_url_does_not_double_up()
    {
        var portrait = HeraldXiAdapter.MapPortrait(Load(), "https://heraldxi.example.test/");

        Assert.NotNull(portrait);
        Assert.DoesNotContain("//portraits", portrait.Url);
    }

    [Fact]
    public void A_character_the_herald_cannot_render_has_no_portrait()
    {
        var dto = Load();
        dto.Appearance!.Renderable = false;

        // The herald 404s the route for these rather than serving a placeholder,
        // so asking for it would only waste a request.
        Assert.Null(HeraldXiAdapter.MapPortrait(dto, Base));
    }

    [Fact]
    public void A_missing_appearance_block_has_no_portrait()
    {
        var dto = Load();
        dto.Appearance = null;

        Assert.Null(HeraldXiAdapter.MapPortrait(dto, Base));
    }

    [Fact]
    public void A_blank_hash_has_no_portrait()
    {
        // Without a version there is no way to tell a changed picture from an
        // unchanged one, which is the whole basis of the refresh.
        var dto = Load();
        dto.Appearance!.Hash = "";

        Assert.Null(HeraldXiAdapter.MapPortrait(dto, Base));
    }

    [Fact]
    public void The_portrait_rides_along_on_the_mapped_character()
    {
        var c = HeraldXiAdapter.Map(Load(), "u", Base);

        Assert.NotNull(c.Portrait);
        Assert.StartsWith("a57c46727615|", c.Portrait.Version);
    }

    // --- the two URLs -------------------------------------------------------

    [Fact]
    public void The_character_links_to_the_herald_and_not_to_the_api()
    {
        // The reported bug. Every card sent a member to a page of JSON, because the
        // URL this adapter fetches was also the URL it stored for linking.
        var c = HeraldXiAdapter.Map(Load(), HeraldXiAdapter.PlayerUrl(Base, "Arwen"), Base);

        Assert.Equal("https://heraldxi.example.test/player/Arwen", c.Url);
        Assert.DoesNotContain("/api/", c.Url!);
    }

    [Fact]
    public void The_api_url_and_the_player_url_are_different_pages()
    {
        Assert.True(HeraldXiAdapter.TryApiUrl(Base, "Arwen", out var api));

        Assert.Equal("https://heraldxi.example.test/api/v1/characters/Arwen", api);
        Assert.NotEqual(api, HeraldXiAdapter.PlayerUrl(Base, "Arwen"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Not A Name")]
    [InlineData("Arwen123")]
    public void No_player_url_for_something_that_is_not_a_name(string name)
    {
        Assert.Null(HeraldXiAdapter.PlayerUrl(Base, name));
    }

    [Fact]
    public void No_player_url_without_a_herald_address()
    {
        Assert.Null(HeraldXiAdapter.PlayerUrl("not a url", "Arwen"));
    }

    // --- the portrait's version ----------------------------------------------

    [Fact]
    public void The_portrait_version_covers_the_equipment_and_not_only_the_hash()
    {
        // The failure this exists for, observed on 2026-08-30: the herald served two
        // different renders of one character under one hash, 040480b55b00, one
        // wearing armour and one wearing none. Keyed on the hash alone, a stored
        // portrait never updates again.
        var geared = Load();
        var stripped = Load();
        stripped.Appearance!.EquipArg = "main=21";

        var a = HeraldXiAdapter.MapPortrait(geared, Base)!;
        var b = HeraldXiAdapter.MapPortrait(stripped, Base)!;

        // Same hash, so the herald says nothing changed.
        Assert.Equal(geared.Appearance!.Hash, stripped.Appearance!.Hash);
        Assert.Equal(a.Url, b.Url);

        // We say otherwise, which is what makes the next refresh fetch the picture.
        Assert.NotEqual(a.Version, b.Version);
        Assert.NotEqual(a.Tag, b.Tag);
    }

    [Fact]
    public void The_portrait_url_is_still_the_one_the_herald_serves()
    {
        // The hash is what its own player pages ask for, so the URL keeps it even
        // though our version key is wider.
        var portrait = HeraldXiAdapter.MapPortrait(Load(), Base)!;

        Assert.Equal($"{Base}/portraits/3.png?v=a57c46727615", portrait.Url);
        Assert.StartsWith("a57c46727615|", portrait.Version);
    }

    [Fact]
    public void The_models_stand_in_when_the_equipment_argument_is_missing()
    {
        // An older herald, or a field renamed. Falling back to the models keeps the
        // version sensitive to equipment rather than silently returning to the hash.
        var dto = Load();
        dto.Appearance!.EquipArg = null;

        var portrait = HeraldXiAdapter.MapPortrait(dto, Base)!;

        Assert.Contains("body=8", portrait.Version);
        Assert.Contains("main=21", portrait.Version);
    }
}

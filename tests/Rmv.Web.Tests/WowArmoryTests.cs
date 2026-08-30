using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Rmv.Web.Herald;

namespace Rmv.Web.Tests;

/// <summary>
/// Parses the real Armory page saved in Fixtures.
///
/// Stored gzipped, unlike the other fixtures, because the page is 730KB and all
/// but 20KB of it is gear, talents and statistics this parser never reads. The
/// bytes are the ones Blizzard served, untouched; `gunzip -c` to read it.
/// </summary>
public class WowArmoryParserTests
{
    private const string Url =
        "https://worldofwarcraft.blizzard.com/en-us/worldsoul/us/armory/character/quelthalas/syfr";

    private static string Page()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "wow-armory-character.html.gz");

        using var file = File.OpenRead(path);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);

        return reader.ReadToEnd();
    }

    private static HeraldCharacter Parsed() => WowArmoryParser.Parse(Page(), Url)!;

    [Fact]
    public void Reads_the_character_out_of_the_real_page()
    {
        var c = Parsed();

        Assert.Equal("Syfr", c.Name);
        Assert.Equal(80, c.Level);
        Assert.Equal("Void Elf", c.Race);
        Assert.Equal(Url, c.Url);
    }

    [Fact]
    public void The_specialisation_is_part_of_the_class()
    {
        // "Death Knight" alone is not what anyone means when they ask what someone
        // plays.
        Assert.Equal("Frost Death Knight", Parsed().Class);
    }

    [Fact]
    public void Realm_carries_the_faction_too()
    {
        // One field on the card for where somebody plays, and in a two-faction game
        // both halves matter.
        Assert.Equal("Quel'Thalas (Alliance)", Parsed().Realm);
    }

    [Fact]
    public void The_guild_comes_across_with_its_accents()
    {
        Assert.Equal("El Cartel de los Murlócs", Parsed().Guild);
    }

    [Fact]
    public void The_title_is_the_prefix_without_its_placeholder()
    {
        // Stored as "Inquisitor {name}". Rendering that template would look like a
        // bug, and it is the same field FFXI puts a title in.
        var rank = Parsed().RealmRank;

        Assert.Equal("Inquisitor", rank);
        Assert.DoesNotContain("{", rank!);
    }

    [Fact]
    public void Achievement_points_are_the_score_and_honourable_kills_the_kills()
    {
        var c = Parsed();

        Assert.Equal(7290, c.RealmPoints);
        Assert.Equal(1273, c.Kills);
    }

    [Fact]
    public void The_last_update_is_a_date_rather_than_a_timestamp()
    {
        Assert.Equal("2026-05-01", Parsed().LastOnline);
    }

    [Fact]
    public void The_portrait_is_the_full_body_render()
    {
        var portrait = Parsed().Portrait;

        Assert.NotNull(portrait);
        Assert.EndsWith("-main-raw.png", portrait.Url);

        // Not the 84 pixel avatar and not the 230 pixel bust, both of which the
        // payload also offers.
        Assert.DoesNotContain("-avatar", portrait.Url);
        Assert.DoesNotContain("-inset", portrait.Url);
    }

    [Fact]
    public void The_render_url_says_nothing_about_the_appearance()
    {
        // Why the version is the picture rather than anything in this payload: the
        // URL carries the character id, so Blizzard reuses it when someone changes
        // armour. See HeraldPortrait.
        var portrait = Parsed().Portrait!;

        Assert.Contains("243313029", portrait.Url);
        Assert.DoesNotContain("146", portrait.Url);
    }

    // --- finding the blob at all ---------------------------------------------

    [Fact]
    public void A_brace_inside_a_string_does_not_end_the_object()
    {
        // The trap this page actually contains: the title is "Inquisitor {name}".
        // Counting braces without skipping strings closes the object early and
        // hands back JSON that does not parse.
        var html = """
            <script>var characterProfileInitialState = {"character":{"name":"Trap",
            "prefix":"Lord {name}","level":70}};</script>
            """;

        var c = WowArmoryParser.Parse(html, "u");

        Assert.NotNull(c);
        Assert.Equal("Trap", c.Name);
        Assert.Equal("Lord", c.RealmRank);
        Assert.Equal(70, c.Level);
    }

    [Fact]
    public void An_escaped_quote_does_not_end_the_string()
    {
        var html =
            """<script>var characterProfileInitialState = {"character":{"name":"Es\"cape","level":1}};</script>""";

        Assert.Equal("Es\"cape", WowArmoryParser.Parse(html, "u")!.Name);
    }

    [Theory]
    // The error page the Armory serves for a character it will not show.
    [InlineData("<html><body>Something went wrong</body></html>")]
    [InlineData("")]
    // Present but truncated, which is what a capped read of a huge page leaves.
    [InlineData("<script>var characterProfileInitialState = {\"character\":{\"name\":\"Cut")]
    // Valid JSON, no character.
    [InlineData("<script>var characterProfileInitialState = {\"summary\":{}};</script>")]
    // A character with no name is not a character.
    [InlineData("<script>var characterProfileInitialState = {\"character\":{\"level\":80}};</script>")]
    public void Anything_that_is_not_a_character_page_is_nothing(string html)
    {
        Assert.Null(WowArmoryParser.Parse(html, "u"));
    }

    [Fact]
    public void A_level_the_page_could_not_mean_is_dropped()
    {
        var html =
            """<script>var characterProfileInitialState = {"character":{"name":"Odd","level":99999}};</script>""";

        Assert.Null(WowArmoryParser.Parse(html, "u")!.Level);
    }
}

/// <summary>
/// Working out which character a member meant, and what happens when the Armory
/// will not show it.
///
/// A WoW name is unique per realm, not per game, so this adapter is the only one
/// here that cannot work from a name alone. Every form a member might reasonably
/// type is covered, because the alternative is telling them they typed it wrong.
/// </summary>
public class WowArmoryAdapterTests
{
    private const string Root = "https://worldofwarcraft.blizzard.com";

    private static WowArmoryAdapter Adapter(StubImageHandler handler) =>
        new(new HeraldFetcher(new HttpClient(handler), NullLogger<HeraldFetcher>.Instance));

    [Theory]
    // The address he pasted, which is the current shape.
    [InlineData("https://worldofwarcraft.blizzard.com/en-us/worldsoul/us/armory/character/quelthalas/syfr")]
    // With a trailing slash, as the page's own canonical link writes it.
    [InlineData("https://worldofwarcraft.blizzard.com/en-us/worldsoul/us/armory/character/quelthalas/syfr/")]
    // The older shape, which still redirects to the new one.
    [InlineData("https://worldofwarcraft.blizzard.com/en-us/character/us/quelthalas/syfr")]
    // What the game itself calls a character from another realm.
    [InlineData("Syfr-Quel'Thalas")]
    [InlineData("syfr-quelthalas")]
    // Half a remembered URL.
    [InlineData("quelthalas/syfr")]
    public void Every_form_a_member_might_type(string typed)
    {
        var who = WowArmoryAdapter.Identify(typed);

        Assert.NotNull(who);
        Assert.Equal("us", who.Region);
        Assert.Equal("quelthalas", WowArmoryAdapter.Slug(who.Realm));
        Assert.Equal("syfr", WowArmoryAdapter.Slug(who.Name));
    }

    [Fact]
    public void The_region_comes_from_a_pasted_address()
    {
        // An EU character resolves against the EU Armory without anyone choosing a
        // setting, because the address already said so.
        var who = WowArmoryAdapter.Identify(
            "https://worldofwarcraft.blizzard.com/en-gb/worldsoul/eu/armory/character/draenor/someone");

        Assert.Equal("eu", who!.Region);
        Assert.Equal("draenor", WowArmoryAdapter.Slug(who.Realm));
    }

    [Theory]
    [InlineData("Syfr")]
    [InlineData("   ")]
    [InlineData("")]
    // A guild page is not a character page.
    [InlineData("https://worldofwarcraft.blizzard.com/en-us/worldsoul/us/armory/guild/quelthalas/rmv")]
    public void A_name_without_a_realm_is_not_enough(string typed)
    {
        Assert.Null(WowArmoryAdapter.Identify(typed));
    }

    [Theory]
    // A realm of more than one word, written every way a member might write it.
    // The split is on the first hyphen because a character name cannot contain
    // one; splitting at the end would look for "Syfr-Emerald" on "Dream".
    [InlineData("Syfr-Emerald Dream", "emerald-dream")]
    [InlineData("Syfr-Emerald-Dream", "emerald-dream")]
    [InlineData("Syfr-Aerie Peak", "aerie-peak")]
    public void A_realm_of_several_words_survives_the_split(string typed, string realmSlug)
    {
        var who = WowArmoryAdapter.Identify(typed);

        Assert.NotNull(who);
        Assert.Equal("syfr", WowArmoryAdapter.Slug(who.Name));
        Assert.Equal(realmSlug, WowArmoryAdapter.Slug(who.Realm));
    }

    [Theory]
    [InlineData("Quel'Thalas", "quelthalas")]
    [InlineData("Aerie Peak", "aerie-peak")]
    [InlineData("Área 52", "area-52")]
    [InlineData("Mal'Ganis", "malganis")]
    [InlineData("Emerald Dream", "emerald-dream")]
    [InlineData("  Burning   Legion  ", "burning-legion")]
    public void Realms_are_slugged_the_way_blizzard_writes_them(string realm, string slug)
    {
        Assert.Equal(slug, WowArmoryAdapter.Slug(realm));
    }

    [Fact]
    public void The_url_is_built_from_the_slugs()
    {
        var who = WowArmoryAdapter.Identify("Syfr-Aerie Peak")!;

        Assert.Equal(
            "https://worldofwarcraft.blizzard.com/en-us/worldsoul/us/armory/character/aerie-peak/syfr",
            WowArmoryAdapter.CharacterUrl(Root, who));
    }

    [Fact]
    public async Task A_character_the_armory_will_not_show_names_the_subscription()
    {
        // The Armory answers 500, not 404, for a character it will not show, and a
        // lapsed subscription is indistinguishable from a misspelling from outside.
        // So the message has to offer both explanations.
        var handler = new StubImageHandler { ForcedStatus = HttpStatusCode.InternalServerError };
        var adapter = Adapter(handler);

        var result = await adapter.FetchCharacterAsync(Root, "Syfr-Quel'Thalas", default);

        Assert.False(result.Ok);
        Assert.Contains("Syfr", result.Error!);
        Assert.Contains("Quel'Thalas", result.Error!);
        // The note itself, not a paraphrase of it, so the message a member reads
        // and the note beside the checkbox cannot drift apart.
        Assert.Contains(adapter.CoverageNote!, result.Error!);
    }

    [Fact]
    public async Task A_bare_name_is_refused_before_anything_is_fetched()
    {
        var handler = new StubImageHandler();

        var result = await Adapter(handler).FetchCharacterAsync(Root, "Syfr", default);

        Assert.False(result.Ok);
        Assert.Contains("realm", result.Error!, StringComparison.OrdinalIgnoreCase);
        // Nobody's server was troubled to find that out.
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_page_that_is_not_a_character_is_reported_as_such()
    {
        var handler = new StubImageHandler
        {
            ContentType = "text/html",
            Body = System.Text.Encoding.UTF8.GetBytes("<html><body>Maintenance</body></html>"),
        };

        var result = await Adapter(handler).FetchCharacterAsync(Root, "quelthalas/syfr", default);

        Assert.False(result.Ok);
        Assert.Contains("does not contain a character", result.Error!);
    }

    [Fact]
    public async Task It_asks_for_the_address_it_built()
    {
        var handler = new StubImageHandler
        {
            ContentType = "text/html",
            Body = System.Text.Encoding.UTF8.GetBytes("nothing"),
        };

        await Adapter(handler).FetchCharacterAsync(Root, "Syfr-Quel'Thalas", default);

        Assert.Equal(
            "https://worldofwarcraft.blizzard.com/en-us/worldsoul/us/armory/character/quelthalas/syfr",
            handler.Requested.Single());
    }

    [Fact]
    public void The_coverage_note_is_what_makes_the_choice_appear()
    {
        // Both the offer to type a sheet in and the note beside it read this one
        // property, so they cannot disagree. The other three heralds list every
        // character on their server and have none.
        IHeraldAdapter armory = Adapter(new StubImageHandler());

        Assert.NotNull(armory.CoverageNote);
        Assert.Contains("subscription", armory.CoverageNote, StringComparison.OrdinalIgnoreCase);

        // Null is the interface's default, so a new herald that lists everyone
        // needs no thought and gets today's behaviour.
        IHeraldAdapter lists = new FakeHeraldAdapter();
        Assert.Null(lists.CoverageNote);
    }
}

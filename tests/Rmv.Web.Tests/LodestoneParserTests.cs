using AngleSharp;
using AngleSharp.Dom;
using Rmv.Web.Herald;

namespace Rmv.Web.Tests;

/// <summary>
/// Parses the real Lodestone pages saved in Fixtures. Hand-written HTML would
/// only prove the parser matches my idea of the markup, which is the thing most
/// likely to be wrong.
///
/// The fixtures are one character: Aoii Aeredel on Exodus, a White Mage 60 whose
/// highest jobs are White Mage and Paladin, both at 60. That tie is deliberate.
/// It is what makes the icon join necessary rather than nice, because picking by
/// level alone would name the wrong job half the time.
/// </summary>
public class LodestoneParserTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static async Task<IDocument> DocAsync(string fixture)
    {
        var context = BrowsingContext.New(AngleSharp.Configuration.Default);
        return await context.OpenAsync(req => req.Content(Fixture(fixture)));
    }

    private static Task<IDocument> CharacterAsync() => DocAsync("lodestone-character.html");

    private static Task<IDocument> ClassJobAsync() => DocAsync("lodestone-classjob.html");

    [Fact]
    public async Task Reads_the_name_world_and_title()
    {
        var c = LodestoneParser.Parse(await CharacterAsync(), await ClassJobAsync(), "https://x.test/c/1/");

        Assert.Equal("Aoii Aeredel", c.Name);
        Assert.Equal("Exodus [Primal]", c.Realm);
        Assert.Equal("Carrier of the Kettle", c.RealmRank);
    }

    [Fact]
    public async Task Names_the_active_job_by_matching_its_icon()
    {
        // Paladin is also 60 and appears first on the page. Only the icon join
        // gets this right.
        var c = LodestoneParser.Parse(await CharacterAsync(), await ClassJobAsync(), "https://x.test/c/1/");

        Assert.Equal("White Mage", c.Class);
        Assert.Equal(60, c.Level);
    }

    [Fact]
    public async Task Falls_back_to_the_highest_job_when_no_icon_matches()
    {
        var jobs = LodestoneParser.Jobs(await ClassJobAsync());

        // A document with no active-job icon at all, standing in for a character
        // with nothing equipped.
        var bare = await DocAsync("lodestone-search.html");

        var picked = LodestoneParser.PickJob(bare, jobs);

        Assert.NotNull(picked);
        Assert.Equal(60, picked.Level);
    }

    [Fact]
    public async Task An_unlevelled_job_has_no_level_rather_than_zero()
    {
        var jobs = LodestoneParser.Jobs(await ClassJobAsync());

        // Gunbreaker reads "-" on this character, which is not level zero.
        var gunbreaker = jobs.Single(j => j.Name == "Gunbreaker");

        Assert.Null(gunbreaker.Level);
        Assert.Equal(60, jobs.Single(j => j.Name == "Paladin").Level);
        Assert.True(jobs.Count > 20, $"expected the full job list, got {jobs.Count}");
    }

    [Fact]
    public async Task Reads_a_labelled_block_and_joins_its_two_lines()
    {
        var c = LodestoneParser.Parse(await CharacterAsync(), await ClassJobAsync(), "https://x.test/c/1/");

        // "Miqo'te<br />Seeker of the Sun / ♀" would read as "Miqo'teSeeker"
        // without turning the break into a separator first.
        Assert.Equal("Miqo'te / Seeker of the Sun / ♀", c.Race);
    }

    [Fact]
    public async Task Reads_a_block_by_label_not_position()
    {
        var doc = await CharacterAsync();

        Assert.Equal("Gridania", LodestoneParser.Block(doc, "City-state"));
        Assert.Null(LodestoneParser.Block(doc, "Not A Real Block"));
    }

    [Fact]
    public async Task Takes_both_images_as_absolute_urls()
    {
        var c = LodestoneParser.Parse(await CharacterAsync(), await ClassJobAsync(), "https://x.test/c/1/");

        Assert.NotNull(c.PortraitUrl);
        Assert.NotNull(c.AvatarUrl);
        Assert.StartsWith("https://img2.finalfantasyxiv.com/", c.PortraitUrl);
        Assert.StartsWith("https://img2.finalfantasyxiv.com/", c.AvatarUrl);
        // Different crops: the portrait is tall, the avatar is the square face.
        Assert.NotEqual(c.PortraitUrl, c.AvatarUrl);
    }

    [Fact]
    public async Task A_character_with_no_free_company_reports_none()
    {
        var c = LodestoneParser.Parse(await CharacterAsync(), await ClassJobAsync(), "https://x.test/c/1/");

        Assert.Null(c.Guild);
    }

    [Fact]
    public async Task Works_without_the_class_job_page()
    {
        // A missing second page costs the job name and nothing else.
        var c = LodestoneParser.Parse(await CharacterAsync(), null, "https://x.test/c/1/");

        Assert.Equal("Aoii Aeredel", c.Name);
        Assert.Equal(60, c.Level);
        Assert.Null(c.Class);
    }

    [Fact]
    public async Task Reads_the_search_results()
    {
        var results = LodestoneParser.Search(await DocAsync("lodestone-search.html"));

        var only = Assert.Single(results);
        Assert.Equal(8868232, only.Id);
        Assert.Equal("Aoii Aeredel", only.Name);
        Assert.Equal("Exodus [Primal]", only.World);
    }

    [Theory]
    [InlineData("https://na.finalfantasyxiv.com/lodestone/character/8868232/", 8868232L)]
    [InlineData("/lodestone/character/8868232/", 8868232L)]
    [InlineData("https://na.finalfantasyxiv.com/lodestone/character/8868232", 8868232L)]
    [InlineData("https://na.finalfantasyxiv.com/lodestone/freecompany/123/", null)]
    [InlineData("Aoii Aeredel", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Finds_a_character_id_in_a_pasted_address(string? href, long? expected)
    {
        Assert.Equal(expected, LodestoneParser.IdFromHref(href));
    }
}

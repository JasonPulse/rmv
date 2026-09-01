using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Rmv.Web.Data;
using Rmv.Web.Herald;
using Rmv.Web.Pages.Tools;
using Rmv.Web.Signature;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Rmv.Web.Tests;

/// <summary>
/// The editor page, driven by hand.
///
/// It cannot be driven in a browser without a Discord sign-in, so this is where the
/// page's own behaviour is checked: that it hands the editor what it needs, that a
/// save goes through the clamps, and that the address it shows a member is the one
/// the public endpoint answers on.
/// </summary>
public class SignaturePageTests : HeraldDatabaseTests
{
    private SignatureService _signatures = null!;
    private SignaturePresets _presets = null!;
    private SignatureFonts _fonts = null!;

    protected override void ConfigureHerald(FakeHeraldAdapter herald) { }

    protected override async Task SeedAsync()
    {
        var root = RepositoryRoot();

        _fonts = new SignatureFonts(Path.Combine(root, "src", "Rmv.Web", "Signature", "Fonts"));
        _presets = new SignaturePresets(Path.Combine(root, "src", "Rmv.Web", "wwwroot"));
        _signatures = new SignatureService(
            Db, new SignatureRenderer(_fonts), _presets, NullLogger<SignatureService>.Instance);

        Db.Characters.Add(new Character
        {
            MemberId = Member.Id,
            GamePresenceId = HeraldGameId,
            Name = "Property",
            Source = CharacterSource.Herald,
            Level = 50,
            Class = "Skald",
            AddedAt = DateTimeOffset.UtcNow,
        });

        await Db.SaveChangesAsync();
    }


    private SignatureModel Page()
    {
        var config = new ConfigurationBuilder().Build();

        var model = new SignatureModel(
            Db,
            new CurrentMember(config, NullLogger<CurrentMember>.Instance,
                new MemberDirectory(Db, config, NullLogger<MemberDirectory>.Instance)),
            _signatures,
            _presets,
            _fonts,
            new Rmv.Web.Herald.HeraldStatTokens(new HeraldRegistry([Herald])));

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Member.DiscordId)], "TestAuth")),
        };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("www.resultsmayvary.org");

        model.PageContext = new PageContext(new ActionContext(
            http, new RouteData(), new PageActionDescriptor(), new ModelStateDictionary()));

        return model;
    }

    [Fact]
    public async Task Opening_it_gives_a_member_an_address_to_paste()
    {
        var page = Page();

        var result = await page.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(page.Signature);

        // The reason anybody is on this page.
        Assert.Equal(
            $"https://www.resultsmayvary.org/sig/{page.Signature.Slug}.png", page.Address);
        Assert.Equal($"[img]{page.Address}[/img]", page.Embed);

        // And the address is the one the endpoint answers on, not a second opinion
        // about what the route is.
        Assert.Equal(SignatureEndpoint.PathFor(page.Signature.Slug), page.ImagePath);
    }

    [Fact]
    public async Task The_editor_is_handed_what_it_needs()
    {
        var page = Page();
        await page.OnGetAsync(default);

        // The design it edits.
        Assert.NotNull(SignatureDesignReader.Read(page.Design));

        // The characters an element can bind to, as JSON its script parses.
        var characters = JsonDocument.Parse(page.CharactersJson).RootElement;
        Assert.Equal(1, characters.GetArrayLength());
        Assert.Contains("Property", characters[0].GetProperty("label").GetString());

        // A label carrying the game, because two characters can share a name.
        Assert.Contains("(", characters[0].GetProperty("label").GetString());

        Assert.NotEmpty(JsonDocument.Parse(page.FontsJson).RootElement.EnumerateArray());
        Assert.NotEmpty(page.Presets);
        Assert.NotEmpty(page.Tokens);
    }

    [Fact]
    public async Task Saving_a_design_from_the_page_redraws_the_picture()
    {
        var page = Page();
        await page.OnGetAsync(default);

        var design = new SignatureDesign(BackgroundKind.Colour, null, "#101820",
        [
            new SignatureElement(20, 30, TextAlign.Left, SignatureFonts.DefaultKey, 20,
                "#ffcc66", null, null, "%User% of Results May Vary"),
        ]);

        page.Design = SignatureService.Serialise(design);

        var result = await page.OnPostSaveAsync(default);

        Assert.IsType<RedirectToPageResult>(result);

        var image = await Db.SignatureImages.AsNoTracking()
            .FirstAsync(i => i.SignatureId == page.Signature!.Id);

        Assert.NotEmpty(image.Bytes);
    }

    [Fact]
    public async Task A_design_the_page_cannot_read_comes_back_as_an_error_not_a_crash()
    {
        var page = Page();
        await page.OnGetAsync(default);

        page.Design = "<not json>";

        var result = await page.OnPostSaveAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(page.Error);
        // And the page still has everything it needs to render itself again.
        Assert.NotNull(page.Address);
    }

    [Fact]
    public async Task Starting_over_puts_the_default_back()
    {
        var page = Page();
        await page.OnGetAsync(default);

        var mangled = new SignatureDesign(BackgroundKind.Colour, null, "#ff0000",
        [
            new SignatureElement(500, 150, TextAlign.Right, SignatureFonts.DefaultKey, 48,
                "#ff00ff", "#00ff00", null, "a mess"),
        ]);

        page.Design = SignatureService.Serialise(mangled);
        await page.OnPostSaveAsync(default);

        var reset = Page();
        var result = await reset.OnPostResetAsync(default);

        Assert.IsType<RedirectToPageResult>(result);

        var stored = SignatureDesignReader.Read(
            (await Db.Signatures.AsNoTracking().FirstAsync(s => s.MemberId == Member.Id)).Design)!;

        // The default's own shape: three lines, the first bound to their character.
        Assert.Equal(3, stored.Elements.Count);
        Assert.NotNull(stored.Elements[0].CharacterId);
    }

    [Fact]
    public async Task The_palette_offers_only_the_heralds_he_has_a_character_on()
    {
        // He has one character, on the fake herald this fixture registers. Offering
        // the Armory's %ItemLevel% to somebody with no WoW character is offering a
        // token that draws nothing on every line it could go on.
        var page = Page();
        await page.OnGetAsync(default);

        Assert.Single(page.HeraldTokens);
        Assert.Equal(Herald.DisplayName, page.HeraldTokens[0].Herald);
    }

    // --- the canvas's text ---------------------------------------------------

    [Fact]
    public async Task The_page_hands_the_canvas_the_words_not_the_tokens()
    {
        var page = Page();
        await page.OnGetAsync(default);

        var lines = JsonDocument.Parse(page.PreviewJson).RootElement
            .EnumerateArray().Select(e => e.GetString()!).ToList();

        // One per line of the default design, and his name in the first one rather
        // than %User%.
        Assert.Equal(SignatureDesignReader.Read(page.Design)!.Elements.Count, lines.Count);
        Assert.DoesNotContain(lines, l => l.Contains('%'));
        Assert.Contains(lines, l => l.Contains("Property"));
    }

    [Fact]
    public async Task Asking_what_a_design_says_answers_with_one_line_each()
    {
        var page = Page();
        await page.OnGetAsync(default);

        var character = await Db.Characters.AsNoTracking().FirstAsync();

        page.Design = SignatureService.Serialise(new SignatureDesign(
            BackgroundKind.Colour, null, "#101820",
        [
            new SignatureElement(10, 10, TextAlign.Left, SignatureFonts.DefaultKey, 18,
                "#fff", null, character.Id, "%Name%%SP%%Class%"),
            new SignatureElement(10, 40, TextAlign.Left, SignatureFonts.DefaultKey, 18,
                "#fff", null, null, "%User% plays %AllChars%"),
        ]));

        var result = Assert.IsType<JsonResult>(await page.OnPostPreviewAsync(default));
        var lines = Assert.IsAssignableFrom<List<string>>(result.Value);

        // The point of the whole thing: what comes back is what the renderer will
        // draw, so a line dragged against it lands where it was put.
        Assert.Equal("Property - Skald", lines[0]);
        Assert.Equal($"{Member.DisplayName} plays 1", lines[1]);
    }

    [Fact]
    public async Task Asking_about_a_design_that_will_not_read_is_refused_not_crashed()
    {
        var page = Page();
        await page.OnGetAsync(default);

        page.Design = "<not json>";
        Assert.IsType<BadRequestResult>(await page.OnPostPreviewAsync(default));

        page.Design = new string('x', SignatureLimits.MaxDesignLength + 1);
        Assert.IsType<BadRequestResult>(await page.OnPostPreviewAsync(default));

        page.Design = null;
        Assert.IsType<BadRequestResult>(await page.OnPostPreviewAsync(default));
    }

    [Fact]
    public async Task A_line_bound_to_somebody_elses_character_says_nothing_about_them()
    {
        // The preview resolves against the caller's own roster, so an id from another
        // member's signature draws blank here as well as in the render.
        var other = await NewMemberAsync();

        Db.Characters.Add(new Character
        {
            MemberId = other.Id,
            GamePresenceId = HeraldGameId,
            Name = "Secret",
            Level = 50,
            AddedAt = DateTimeOffset.UtcNow,
        });

        await Db.SaveChangesAsync();

        var theirs = await Db.Characters.AsNoTracking().FirstAsync(c => c.Name == "Secret");

        var page = Page();
        await page.OnGetAsync(default);

        page.Design = SignatureService.Serialise(new SignatureDesign(
            BackgroundKind.Colour, null, "#101820",
        [
            new SignatureElement(10, 10, TextAlign.Left, SignatureFonts.DefaultKey, 18,
                "#fff", null, theirs.Id, "[%Name%]"),
        ]));

        var result = Assert.IsType<JsonResult>(await page.OnPostPreviewAsync(default));

        Assert.Equal("[]", Assert.IsAssignableFrom<List<string>>(result.Value)[0]);
    }

    [Fact]
    public async Task A_background_belonging_to_somebody_else_is_not_found()
    {
        // The editor asks for its own backgrounds by id through a page handler, so the
        // id is a number a browser could change.
        var theirs = await SomebodyElsesBackgroundAsync();

        var result = await Page().OnGetBackgroundAsync(theirs.Id, default);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Removing_a_background_belonging_to_somebody_else_does_nothing()
    {
        var theirs = await SomebodyElsesBackgroundAsync();

        await Page().OnPostRemoveBackgroundAsync(theirs.Id, default);

        Assert.True(await Db.SignatureBackgrounds.AnyAsync(b => b.Id == theirs.Id));
    }

    // --- uploads -------------------------------------------------------------

    [Fact]
    public async Task An_upload_is_stored_at_canvas_size_whatever_arrived()
    {
        // His storage worry, checked: a large picture comes out bounded by the canvas.
        var big = Wallpaper(2400, 1200);

        var outcome = await _signatures.AddBackgroundAsync(
            Member, new MemoryStream(big), big.Length, "holiday photo.png", default);

        Assert.True(outcome.Ok, outcome.Error);

        var stored = await Db.SignatureBackgrounds.AsNoTracking()
            .FirstAsync(b => b.MemberId == Member.Id);

        Assert.Equal(SignatureLimits.Width, stored.Width);
        Assert.Equal(SignatureLimits.Height, stored.Height);
        Assert.Equal("image/png", stored.ContentType);
        Assert.True(stored.Bytes.Length < big.Length, "stored bigger than the upload");
        Assert.Equal("holiday photo", stored.Name);
    }

    [Fact]
    public async Task Only_two_backgrounds_per_member()
    {
        for (var i = 0; i < SignatureLimits.MaxBackgrounds; i++)
        {
            var bytes = Wallpaper(600, 200);
            var ok = await _signatures.AddBackgroundAsync(
                Member, new MemoryStream(bytes), bytes.Length, $"one {i}", default);
            Assert.True(ok.Ok, ok.Error);
        }

        var extra = Wallpaper(600, 200);
        var refused = await _signatures.AddBackgroundAsync(
            Member, new MemoryStream(extra), extra.Length, "one too many", default);

        Assert.False(refused.Ok);
        Assert.Contains("limit", refused.Error);
        Assert.Equal(SignatureLimits.MaxBackgrounds,
            await Db.SignatureBackgrounds.CountAsync(b => b.MemberId == Member.Id));
    }

    [Theory]
    // What matters is what is inside the file, not what it is called.
    [InlineData("<!DOCTYPE html><script>alert(1)</script>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script/></svg>")]
    [InlineData("not a picture")]
    public async Task Something_that_is_not_a_picture_is_refused(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);

        var outcome = await _signatures.AddBackgroundAsync(
            Member, new MemoryStream(bytes), bytes.Length, "sneaky.png", default);

        Assert.False(outcome.Ok);
        Assert.False(await Db.SignatureBackgrounds.AnyAsync(b => b.MemberId == Member.Id));
    }

    [Fact]
    public async Task An_empty_upload_is_refused()
    {
        var outcome = await _signatures.AddBackgroundAsync(
            Member, new MemoryStream([]), 0, "nothing.png", default);

        Assert.False(outcome.Ok);
        Assert.Contains("empty", outcome.Error);
    }

    [Fact]
    public async Task A_lying_length_does_not_get_past_the_cap()
    {
        // The declared length is a claim. This one says it is small and then streams
        // more than the cap.
        var bytes = new byte[Rmv.Web.Gallery.ImageProbe.MaxBytes + 1024];
        Wallpaper(60, 20).CopyTo(bytes, 0);

        var outcome = await _signatures.AddBackgroundAsync(
            Member, new MemoryStream(bytes), declaredLength: 1024, "big.png", default);

        Assert.False(outcome.Ok);
        Assert.Contains("limit", outcome.Error);
    }

    /// <summary>
    /// A background belonging to another member, for the two checks that an id from
    /// somewhere else is not found rather than found and then refused.
    /// </summary>
    private async Task<SignatureBackground> SomebodyElsesBackgroundAsync()
    {
        var other = await NewMemberAsync();

        Db.SignatureBackgrounds.Add(new SignatureBackground
        {
            MemberId = other.Id,
            Bytes = [1, 2, 3],
            ContentType = "image/png",
            Width = SignatureLimits.Width,
            Height = SignatureLimits.Height,
            Name = "Theirs",
            UploadedAt = DateTimeOffset.UtcNow,
        });

        await Db.SaveChangesAsync();

        return await Db.SignatureBackgrounds.AsNoTracking()
            .FirstAsync(b => b.MemberId == other.Id);
    }

    /// <summary>A real PNG of the given size, with something in it to compress.</summary>
    private static byte[] Wallpaper(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32(
                    (byte)(x % 256), (byte)(y % 256), (byte)((x + y) % 256));
            }
        }

        using var png = new MemoryStream();
        image.SaveAsPng(png);

        return png.ToArray();
    }
}

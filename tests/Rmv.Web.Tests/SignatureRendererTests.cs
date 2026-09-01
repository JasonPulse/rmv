using Rmv.Web.Data;
using Rmv.Web.Signature;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Rmv.Web.Tests;

/// <summary>
/// Drawing a signature.
///
/// Every assertion is about the bytes that come out, because that is what a forum
/// embeds. The renders are also written to the test output so a person can look at
/// one; a signature that passes its assertions and looks wrong is still wrong.
///
/// The fonts come from the app's own folder, found the same way the view tests find
/// the views, so this exercises the real face rather than a stub.
/// </summary>
public class SignatureRendererTests
{
    private static string FontRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, "src", "Rmv.Web", "Signature", "Fonts")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        return Path.Combine(dir.FullName, "src", "Rmv.Web", "Signature", "Fonts");
    }

    private static readonly SignatureFonts Fonts = new(FontRoot());

    private static SignatureRenderer Renderer() => new(Fonts);

    private static Member Him() => new() { DisplayName = "property_x", Alias = "Property" };

    private static IReadOnlyList<Character> Roster() =>
    [
        new()
        {
            Id = 1, Name = "Property", Level = 50, Class = "Skald", Race = "Norseman",
            Realm = "Midgard", Guild = "Results May Vary", RealmRank = "8L0",
            Score = 1_234_567, Kills = 12_345, LastOnline = "2026-05-01",
            AddedAt = new DateTimeOffset(2001, 10, 10, 0, 0, 0, TimeSpan.Zero),
            GamePresenceId = 1, Game = new GamePresence { Id = 1, Game = "Dark Age of Camelot" },
        },
        new()
        {
            Id = 2, Name = "Milliennial", Level = 99, Class = "MNK 99 / WHM 49",
            Realm = "Windurst", Score = 2_100, AddedAt = DateTimeOffset.UtcNow,
            GamePresenceId = 2, Game = new GamePresence { Id = 2, Game = "Final Fantasy XI" },
        },
    ];

    /// <summary>Writes a render next to the test assembly, for looking at.</summary>
    private static byte[] Save(string name, byte[] png)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"signature-{name}.png");
        File.WriteAllBytes(path, png);

        return png;
    }

    private static Image<Rgba32> Decode(byte[] png) => Image.Load<Rgba32>(png);

    [Fact]
    public void The_default_design_renders_a_signature_sized_png()
    {
        var png = Save("default", Renderer().Render(SignatureDesign.Default(1), Him(), Roster()));

        using var image = Decode(png);

        Assert.Equal(SignatureLimits.Width, image.Width);
        Assert.Equal(SignatureLimits.Height, image.Height);

        // A forum embeds this. It has to be a PNG by its own magic number, not by
        // what we called it.
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
    }

    [Fact]
    public void It_is_small_enough_to_embed_ten_times()
    {
        // The constraint he set: ten forum signatures loading at once must not be a
        // spike. Ten of these is well under a megabyte, and none of them costs a
        // render because the bytes are stored.
        var png = Renderer().Render(SignatureDesign.Default(1), Him(), Roster());

        Assert.InRange(png.Length, 1_000, 120_000);
    }

    [Fact]
    public void Something_was_actually_drawn()
    {
        // A canvas of flat background is what a broken font or a swallowed exception
        // looks like, and it would pass every other assertion here.
        var design = SignatureDesign.Default(1);
        var png = Renderer().Render(design, Him(), Roster());

        using var drawn = Decode(png);
        using var blank = Decode(Renderer().Render(design with { Elements = [] }, Him(), Roster()));

        var different = 0;

        for (var y = 0; y < drawn.Height; y++)
        {
            for (var x = 0; x < drawn.Width; x++)
            {
                if (drawn[x, y] != blank[x, y])
                {
                    different++;
                }
            }
        }

        // Three lines of text at these sizes cover thousands of pixels.
        Assert.True(different > 2_000, $"only {different} pixels differ from an empty canvas");
    }

    [Fact]
    public void The_text_colour_reaches_the_image()
    {
        var design = SignatureDesign.Default(1) with
        {
            Colour = "#000000",
            Elements =
            [
                new SignatureElement(10, 10, TextAlign.Left, SignatureFonts.DefaultKey, 40,
                    "#ff0000", null, 1, "IIII"),
            ],
        };

        using var image = Decode(Renderer().Render(design, Him(), Roster()));

        var red = 0;

        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (image[x, y].R > 200 && image[x, y].G < 60 && image[x, y].B < 60)
                {
                    red++;
                }
            }
        }

        Assert.True(red > 100, $"only {red} red pixels");
    }

    [Fact]
    public void A_background_image_is_drawn_under_the_text()
    {
        // A solid green background at the canvas size, so its presence is obvious.
        using var art = new Image<Rgba32>(SignatureLimits.Width, SignatureLimits.Height,
            new Rgba32(0, 200, 0));
        using var bytes = new MemoryStream();
        art.SaveAsPng(bytes);

        // Outlined text, because that is what a picture background is for: the
        // outline is stroked first and filled over, so only its outer half shows.
        var design = SignatureDesign.Default(1) with
        {
            Background = BackgroundKind.Preset,
            Elements = SignatureDesign.Default(1).Elements
                .Select(e => e with { Outline = "#000000" })
                .ToList(),
        };

        var png = Save("background", Renderer().Render(design, Him(), Roster(), bytes.ToArray()));

        using var image = Decode(png);

        // The corner is background, not text.
        Assert.True(image[2, 2].G > 150, $"corner was {image[2, 2]}");
    }

    [Fact]
    public void A_background_that_is_the_wrong_size_is_resized_rather_than_refused()
    {
        using var art = new Image<Rgba32>(64, 64, new Rgba32(0, 0, 200));
        using var bytes = new MemoryStream();
        art.SaveAsPng(bytes);

        var design = SignatureDesign.Default(1) with { Background = BackgroundKind.Upload };

        using var image = Decode(Renderer().Render(design, Him(), Roster(), bytes.ToArray()));

        Assert.Equal(SignatureLimits.Width, image.Width);
        Assert.True(image[SignatureLimits.Width - 2, 2].B > 150);
    }

    [Theory]
    // Nothing a member or a corrupt row can put in the background may break the
    // render: a forum shows a broken image and nobody knows why.
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4 })]
    public void A_background_that_will_not_decode_leaves_the_colour(byte[] rubbish)
    {
        var design = SignatureDesign.Default(1) with { Background = BackgroundKind.Preset };

        var png = Renderer().Render(design, Him(), Roster(), rubbish);

        using var image = Decode(png);
        Assert.Equal(SignatureLimits.Width, image.Width);
    }

    [Fact]
    public void An_element_bound_to_a_second_character_draws_that_character()
    {
        // What replaces v1's %AC family: a second character is a second element.
        var design = SignatureDesign.Default(1) with
        {
            Elements =
            [
                new SignatureElement(12, 12, TextAlign.Left, SignatureFonts.DefaultKey, 18,
                    "#ffffff", "#000000", 1, "%Name% of %Game%"),
                new SignatureElement(12, 44, TextAlign.Left, SignatureFonts.DefaultKey, 18,
                    "#ffffff", "#000000", 2, "%Name% of %Game%"),
                new SignatureElement(12, 120, TextAlign.Left, SignatureFonts.DefaultKey, 14,
                    "#c0b8a4", "#000000", null,
                    "%User% has played %AllChars% characters in %AllGames% games"),
            ],
        };

        var png = Save("two-characters", Renderer().Render(design, Him(), Roster()));

        // Both lines drew, so the image differs from one with either removed.
        using var both = Decode(png);
        using var one = Decode(Renderer().Render(
            design with { Elements = design.Elements.Take(1).ToList() }, Him(), Roster()));

        Assert.NotEqual(0, Difference(both, one));
    }

    private static int Difference(Image<Rgba32> a, Image<Rgba32> b)
    {
        var count = 0;

        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                if (a[x, y] != b[x, y])
                {
                    count++;
                }
            }
        }

        return count;
    }

    // --- what a member can push at it ----------------------------------------

    [Fact]
    public void A_position_outside_the_canvas_is_pulled_inside()
    {
        var design = SignatureDesign.Default(1) with
        {
            Elements =
            [
                new SignatureElement(-9999, -9999, TextAlign.Left, SignatureFonts.DefaultKey, 20,
                    "#ffffff", null, 1, "Nowhere"),
                new SignatureElement(99999, 99999, TextAlign.Left, SignatureFonts.DefaultKey, 20,
                    "#ffffff", null, 1, "Nowhere"),
            ],
        };

        using var image = Decode(Renderer().Render(design, Him(), Roster()));

        Assert.Equal(SignatureLimits.Width, image.Width);
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(0)]
    [InlineData(10_000)]
    public void A_font_size_that_is_not_a_font_size_is_clamped(int size)
    {
        var design = SignatureDesign.Default(1) with
        {
            Elements =
            [
                new SignatureElement(10, 10, TextAlign.Left, SignatureFonts.DefaultKey, size,
                    "#ffffff", null, 1, "%Name%"),
            ],
        };

        var png = Renderer().Render(design, Him(), Roster());

        Assert.InRange(png.Length, 1_000, 400_000);
    }

    [Fact]
    public void Every_shipped_face_loads_and_draws()
    {
        // Five files under the Open Font License. A face whose file is missing or
        // will not parse is a font in the picker that silently draws in Vollkorn, so
        // this checks each one both loads and produces different pixels.
        Assert.Equal(5, Fonts.Keys.Count);

        var seen = new Dictionary<string, string>();

        foreach (var key in Fonts.Keys)
        {
            Assert.True(Fonts.Has(key), key);

            var design = SignatureDesign.Default(1) with
            {
                Elements =
                [
                    new SignatureElement(10, 20, TextAlign.Left, key, 24,
                        "#ffffff", null, 1, "Property - Level 50"),
                ],
            };

            var png = Save($"font-{key}", Renderer().Render(design, Him(), Roster()));
            var digest = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(png))[..16];

            // Two keys drawing identical pixels means one of them fell back.
            Assert.DoesNotContain(digest, seen.Values);
            seen[key] = digest;
        }
    }

    [Fact]
    public void A_font_nobody_has_falls_back_rather_than_failing()
    {
        // v1 appended ".ttf" to whatever the form sent and opened it, which is a
        // directory traversal in a font picker.
        var design = SignatureDesign.Default(1) with
        {
            Elements =
            [
                new SignatureElement(10, 10, TextAlign.Left, "../../../etc/passwd", 20,
                    "#ffffff", null, 1, "%Name%"),
            ],
        };

        var png = Renderer().Render(design, Him(), Roster());

        using var image = Decode(png);
        Assert.Equal(SignatureLimits.Width, image.Width);
        Assert.False(Fonts.Has("../../../etc/passwd"));
    }

    [Theory]
    [InlineData("not a colour")]
    [InlineData("")]
    [InlineData("#gggggg")]
    [InlineData("rgb(1,2,3); DROP TABLE")]
    public void A_colour_that_is_not_a_colour_falls_back(string colour)
    {
        var design = SignatureDesign.Default(1) with
        {
            Colour = colour,
            Elements =
            [
                new SignatureElement(10, 10, TextAlign.Left, SignatureFonts.DefaultKey, 20,
                    colour, colour, 1, "%Name%"),
            ],
        };

        using var image = Decode(Renderer().Render(design, Him(), Roster()));

        Assert.Equal(SignatureLimits.Width, image.Width);
    }

    [Fact]
    public void More_elements_than_the_limit_are_ignored_rather_than_drawn()
    {
        var many = Enumerable.Range(0, 200)
            .Select(i => new SignatureElement(5, i, TextAlign.Left, SignatureFonts.DefaultKey, 12,
                "#ffffff", "#000000", 1, $"line {i} %Name%"))
            .ToList();

        var design = SignatureDesign.Default(1) with { Elements = many };

        var png = Renderer().Render(design, Him(), Roster());
        var capped = Renderer().Render(
            design with { Elements = many.Take(SignatureLimits.MaxElements).ToList() },
            Him(), Roster());

        // Identical, because everything past the cap was never drawn.
        Assert.Equal(capped, png);
    }

    [Fact]
    public void A_template_long_enough_to_be_a_denial_of_service_is_cut()
    {
        var design = SignatureDesign.Default(1) with
        {
            Elements =
            [
                new SignatureElement(0, 0, TextAlign.Left, SignatureFonts.DefaultKey,
                    SignatureLimits.MaxFontSize, "#ffffff", "#000000", 1,
                    new string('W', 5_000)),
            ],
        };

        var started = System.Diagnostics.Stopwatch.StartNew();
        var png = Renderer().Render(design, Him(), Roster());
        started.Stop();

        Assert.InRange(png.Length, 1_000, 400_000);
        // Generous, because a loaded CI machine is not a benchmark. The point is
        // that it finishes at all.
        Assert.True(started.ElapsedMilliseconds < 5_000, $"took {started.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void A_member_with_no_characters_still_gets_a_signature()
    {
        // A new member's default design, before they have added anything.
        var png = Save("empty", Renderer().Render(SignatureDesign.Default(null), Him(), []));

        using var image = Decode(png);
        Assert.Equal(SignatureLimits.Width, image.Width);
    }

    [Fact]
    public void The_same_design_and_data_render_the_same_bytes()
    {
        // The cache and its version depend on this: a render that varied run to run
        // would change the stored digest every pass and make every browser refetch.
        var a = Renderer().Render(SignatureDesign.Default(1), Him(), Roster());
        var b = Renderer().Render(SignatureDesign.Default(1), Him(), Roster());

        Assert.Equal(a, b);
    }

    [Fact]
    public void Rendering_is_fast_enough_to_be_uninteresting()
    {
        // His other constraint. One render per member per day, so this only has to
        // be cheap rather than fast, but it is worth knowing the number.
        var renderer = Renderer();
        var design = SignatureDesign.Default(1);
        var member = Him();
        var roster = Roster();

        renderer.Render(design, member, roster);

        var started = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            renderer.Render(design, member, roster);
        }
        started.Stop();

        var each = started.Elapsed.TotalMilliseconds / 20;
        Assert.True(each < 250, $"{each:F1}ms per render");
    }
}

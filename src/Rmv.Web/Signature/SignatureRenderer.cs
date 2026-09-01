using Rmv.Web.Data;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Rmv.Web.Signature;

/// <summary>
/// Draws a signature to PNG bytes.
///
/// Nothing here touches the network or the database, which is the whole point: v1
/// fetched the herald and rendered inside the request for the image, with
/// Cache-Control set to no-cache, so every forum page view anybody loaded cost an
/// outbound scrape and a render. This runs when a design or its data changes, and
/// what a forum asks for is the stored bytes.
///
/// Everything a member controls is clamped, because this is where their numbers
/// meet an allocator: the canvas is fixed, the element count is capped, positions
/// are pulled inside the canvas, sizes are clamped and colours are parsed rather
/// than trusted. A design that arrived from anywhere still renders something
/// 520x160.
/// </summary>
public sealed class SignatureRenderer(SignatureFonts fonts)
{
    /// <summary>
    /// Compression 6 rather than the maximum. At this size the difference is a few
    /// kilobytes and the render happens on a background pass, so neither end of the
    /// tradeoff matters much; the middle is fine.
    /// </summary>
    private static readonly PngEncoder Encoder = new()
    {
        CompressionLevel = PngCompressionLevel.Level6,
        ColorType = PngColorType.Rgb,
    };

    /// <param name="background">
    /// The preset or upload to draw under the text, or null for the flat colour.
    /// Bytes rather than a path: an upload lives in Postgres and a preset is read
    /// once, and neither should be a filename this class interprets.
    /// </param>
    public byte[] Render(
        SignatureDesign design,
        Member member,
        IReadOnlyList<Character> roster,
        byte[]? background = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(roster);

        using var image = new Image<Rgba32>(SignatureLimits.Width, SignatureLimits.Height);

        Paint(image, design, background);

        foreach (var element in design.Elements.Take(SignatureLimits.MaxElements))
        {
            Draw(image, element, SignatureData.Subject(member, roster, element.CharacterId));
        }

        using var png = new MemoryStream();
        image.Save(png, Encoder);

        return png.ToArray();
    }

    /// <summary>
    /// The background: a flat colour, or an image stretched to the canvas.
    ///
    /// Stretched rather than tiled or cropped, because every v1 preset was already
    /// the canvas size and an upload is re-encoded to it on the way in. Anything
    /// that arrives the wrong shape is a mistake worth showing rather than hiding.
    /// </summary>
    private static void Paint(Image<Rgba32> image, SignatureDesign design, byte[]? background)
    {
        image.Mutate(x => x.BackgroundColor(Parse(design.Colour) ?? new Color(new Rgba32(10, 12, 18))));

        if (design.Background == BackgroundKind.Colour || background is null || background.Length == 0)
        {
            return;
        }

        try
        {
            using var art = Image.Load<Rgba32>(background);

            if (art.Width != image.Width || art.Height != image.Height)
            {
                art.Mutate(a => a.Resize(image.Width, image.Height));
            }

            image.Mutate(x => x.DrawImage(art, 1f));
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException or InvalidImageContentException)
        {
            // A background that will not decode leaves the flat colour. A signature
            // with no picture is better than a signature that is an error page.
        }
    }

    private void Draw(Image<Rgba32> image, SignatureElement element, SignatureSubject subject)
    {
        var text = SignatureTokens.Resolve(element.Template, subject);

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // One line per element. A newline in a template would let one element grow
        // as tall as the canvas however small its font, so it becomes a space and
        // the member positions a second element instead.
        text = text.ReplaceLineEndings(" ");

        if (text.Length > SignatureLimits.MaxTemplate * 2)
        {
            text = text[..(SignatureLimits.MaxTemplate * 2)];
        }

        var font = fonts.Get(element.Font, element.Size);

        var x = Math.Clamp(element.X, 0, SignatureLimits.Width);
        var y = Math.Clamp(element.Y, 0, SignatureLimits.Height);

        var options = new RichTextOptions(font)
        {
            Origin = new PointF(x, y),
            HorizontalAlignment = element.Align switch
            {
                TextAlign.Centre => HorizontalAlignment.Center,
                TextAlign.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left,
            },
            // The y a member dragged to is the top of the text, not its baseline.
            // Dragging something to y=0 and having it vanish above the canvas is the
            // kind of thing that makes an editor feel broken.
            VerticalAlignment = VerticalAlignment.Top,
        };

        var colour = Parse(element.Colour) ?? Color.White;
        var outline = Parse(element.Outline);

        // The outline is stroked first and the fill goes over it, so only the outer
        // half of the stroke shows. Stroking and filling in one pass centres the
        // stroke on the glyph outline instead, which at 13 pixels eats the letters
        // and turns a signature into a smudge.
        //
        // Worth having only over a picture. On a flat background it adds nothing and
        // costs legibility, which is why the default design leaves it off.
        if (outline is { } edge)
        {
            image.Mutate(c => c.DrawText(options, text, Pens.Solid(edge, 2f)));
        }

        image.Mutate(c => c.DrawText(options, text, colour));
    }

    /// <summary>
    /// A colour a member typed, or null.
    ///
    /// Parsed, never interpolated: this string came from a form, and ImageSharp's
    /// own parser is the thing that decides what is a colour.
    /// </summary>
    private static Color? Parse(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Color.TryParseHex(value, out var parsed)
            ? parsed
            : null;
}

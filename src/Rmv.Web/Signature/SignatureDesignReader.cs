using System.Text.Json;
using System.Text.Json.Serialization;
using SixLabors.ImageSharp;

namespace Rmv.Web.Signature;

/// <summary>
/// Turns the JSON a browser sent into a design the renderer will draw, and refuses
/// to be surprised by any of it.
///
/// This is the boundary. Everything past it is trusted, so everything here is
/// parsed and clamped: element count, positions, sizes, fonts against an allowlist,
/// colours through a parser, templates truncated, and the character binding checked
/// against the ones the member actually owns. A design that came from a form, a
/// script, or a row somebody edited by hand renders something 520x160 either way.
///
/// Clamping rather than rejecting, deliberately. A member dragging an element two
/// pixels off the canvas should get it back on the canvas, not a validation error
/// about a coordinate they cannot see.
/// </summary>
public static class SignatureDesignReader
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: true) },
        // A design with an unexpected field is a design from a newer editor or a
        // typo, and neither is worth failing a save over.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    /// <summary>The design, or null when the JSON is not one.</summary>
    public static SignatureDesign? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > SignatureLimits.MaxDesignLength)
        {
            return null;
        }

        try
        {
            var design = JsonSerializer.Deserialize<SignatureDesign>(json, Json);

            // Elements is non-nullable on the record but JSON can still omit it.
            return design is null ? null : design with { Elements = design.Elements ?? [] };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pulls a design inside every limit.
    /// </summary>
    /// <param name="ownedCharacterIds">
    /// The member's own characters. An element bound to anything else is unbound
    /// rather than refused: it is what a design looks like after somebody deletes a
    /// character, and it is also what a copied design from another member looks
    /// like. Either way it must not draw somebody else's data.
    /// </param>
    /// <param name="presetKeys">The backgrounds that exist. Anything else is no background.</param>
    public static SignatureDesign Clamp(
        SignatureDesign design,
        IReadOnlySet<int> ownedCharacterIds,
        IReadOnlyCollection<string> presetKeys)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(ownedCharacterIds);
        ArgumentNullException.ThrowIfNull(presetKeys);

        var background = Enum.IsDefined(design.Background) ? design.Background : BackgroundKind.Colour;
        var key = (design.BackgroundKey ?? "").Trim();

        // A key that names nothing is no background rather than a broken one.
        if (background == BackgroundKind.Preset && !presetKeys.Contains(key))
        {
            background = BackgroundKind.Colour;
            key = "";
        }

        // An upload is referred to by its row id. Ownership is checked when the bytes
        // are read, so a number here is only a number.
        if (background == BackgroundKind.Upload && !int.TryParse(key, out _))
        {
            background = BackgroundKind.Colour;
            key = "";
        }

        var elements = design.Elements
            .Take(SignatureLimits.MaxElements)
            .Select(e => Clamp(e, ownedCharacterIds))
            // An element with nothing to say is an element somebody deleted the text
            // out of, and drawing nothing is not worth storing.
            .Where(e => e.Template.Length > 0)
            .ToList();

        return new SignatureDesign(
            background,
            background == BackgroundKind.Colour ? null : key,
            Colour(design.Colour) ?? "#0a0c12",
            elements);
    }

    private static SignatureElement Clamp(SignatureElement e, IReadOnlySet<int> owned)
    {
        var template = (e.Template ?? "").ReplaceLineEndings(" ").Trim();

        if (template.Length > SignatureLimits.MaxTemplate)
        {
            template = template[..SignatureLimits.MaxTemplate];
        }

        return new SignatureElement(
            X: Math.Clamp(e.X, 0, SignatureLimits.Width),
            Y: SignatureLimits.TopFor(e.Y, Size(e.Size)),
            Align: Enum.IsDefined(e.Align) ? e.Align : TextAlign.Left,
            // Checked against the real allowlist by the renderer, which owns the
            // faces. Kept as sent so a font added later starts working for a design
            // that already names it.
            Font: string.IsNullOrWhiteSpace(e.Font) ? SignatureFonts.DefaultKey : e.Font.Trim(),
            Size: Size(e.Size),
            Colour: Colour(e.Colour) ?? "#ffffff",
            Outline: Colour(e.Outline),
            CharacterId: e.CharacterId is { } id && owned.Contains(id) ? id : null,
            Template: template);
    }

    private static int Size(int size) =>
        Math.Clamp(size, SignatureLimits.MinFontSize, SignatureLimits.MaxFontSize);

    /// <summary>
    /// A colour, normalised, or null.
    ///
    /// Parsed by the same parser the renderer uses, so a colour that saves is a
    /// colour that draws. Re-emitted in one form rather than stored as typed, which
    /// keeps the source digest stable when somebody writes the same colour two ways.
    /// </summary>
    private static string? Colour(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Color.TryParseHex(value.Trim(), out var parsed))
        {
            return null;
        }

        var rgba = parsed.ToPixel<SixLabors.ImageSharp.PixelFormats.Rgba32>();

        return $"#{rgba.R:x2}{rgba.G:x2}{rgba.B:x2}";
    }
}

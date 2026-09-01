using SixLabors.Fonts;

namespace Rmv.Web.Signature;

/// <summary>
/// The faces a signature may be drawn in, loaded once.
///
/// An allowlist keyed by a short name, because the font in a design is a string a
/// member's browser sent us. v1 took the field straight from the form and appended
/// ".ttf" to a filesystem path, which is a directory traversal wearing a font
/// picker; asking this class for a key it does not have gets the default instead.
///
/// Curated rather than the 116 out of the v1 backup. Those are dafont-era freeware
/// whose terms usually exclude exactly this use, server-side rasterisation on a
/// public site, and the site already ships Vollkorn under the SIL Open Font
/// License. The licence travels with the file; see Signature/Fonts/OFL.txt.
/// </summary>
public sealed class SignatureFonts
{
    public const string DefaultKey = "vollkorn";

    private readonly FontCollection _collection = new();
    private readonly Dictionary<string, FontFamily> _families = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="root">
    /// Where the files are. The content root in the app, the test's own copy in
    /// tests, which is why it is a parameter rather than a constant.
    /// </param>
    public SignatureFonts(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        // One entry per key. The filename is here and nowhere else, so a font that
        // fails to load is a startup problem rather than a broken signature later.
        Add("vollkorn", Path.Combine(root, "Vollkorn[wght].ttf"));

        if (!_families.ContainsKey(DefaultKey))
        {
            throw new InvalidOperationException(
                $"The default signature font is missing from {root}. Nothing can be drawn without it.");
        }
    }

    /// <summary>The keys a member may choose between, for the editor.</summary>
    public IReadOnlyCollection<string> Keys => _families.Keys;

    /// <summary>
    /// A font at a size, falling back to the default face for a key we do not have.
    ///
    /// The size is clamped here as well as on the design, because this is the last
    /// place before a rasteriser is asked for a glyph the size of the canvas.
    /// </summary>
    public Font Get(string? key, int size)
    {
        var family = key is not null && _families.TryGetValue(key, out var found)
            ? found
            : _families[DefaultKey];

        var points = Math.Clamp(size, SignatureLimits.MinFontSize, SignatureLimits.MaxFontSize);

        return family.CreateFont(points, FontStyle.Regular);
    }

    public bool Has(string? key) => key is not null && _families.ContainsKey(key);

    private void Add(string key, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        _families[key] = _collection.Add(path);
    }
}

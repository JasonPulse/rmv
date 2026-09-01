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
/// Five rather than the 116 out of the backup. Those are dafont-era freeware whose
/// terms usually exclude exactly this use, server-side rasterisation on a public
/// site. These are all under the SIL Open Font License, and each family's own
/// licence sits beside it in Signature/Fonts: they are the same licence but not the
/// same copyright line, and one shared file would be a claim about somebody else's
/// font.
///
/// The spread is what a signature needs rather than what a foundry would sell: a
/// text serif, inscriptional capitals, a blackletter, a condensed sans for numbers,
/// and an old print face.
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
        //
        // The variable fonts load at their default instance, which is the regular
        // weight. SixLabors.Fonts 1.0 does not read the weight axis, so a bold face
        // would have to be a separate file; nobody has asked, and a signature is
        // mostly one size of one face.
        Add("vollkorn", Path.Combine(root, "Vollkorn[wght].ttf"));
        Add("cinzel", Path.Combine(root, "Cinzel[wght].ttf"));
        Add("blackletter", Path.Combine(root, "UnifrakturMaguntia-Book.ttf"));
        Add("oswald", Path.Combine(root, "Oswald[wght].ttf"));
        Add("imfell", Path.Combine(root, "IMFellEnglish-Regular.ttf"));

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

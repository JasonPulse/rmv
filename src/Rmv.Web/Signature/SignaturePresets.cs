namespace Rmv.Web.Signature;

/// <param name="Key">The filename without its extension, which is what a design stores.</param>
/// <param name="Name">What the picker calls it.</param>
public sealed record SignaturePreset(string Key, string Name, string Path, string Thumbnail);

/// <summary>
/// The backgrounds shipped with the site, from the 2014 generator's own set.
///
/// DAoC screenshots and gradients carrying the realm knot, all already 520x160,
/// which is the canvas. They are the guild's own history, which is why they are
/// worth keeping rather than replacing with something tasteful.
///
/// An allowlist, keyed by name. A design stores a key and this class turns it into
/// bytes; nothing anywhere turns a member's string into a path. The old one did
/// exactly that, concatenating the posted value onto a directory and calling
/// file_exists on the result.
/// </summary>
public sealed class SignaturePresets
{
    /// <summary>
    /// The set, in the order the picker shows them: pictures first, then the
    /// gradients, because somebody choosing a background is looking for a picture.
    ///
    /// Named by hand. "blackbluerightknot" is what the file is called and not what
    /// anybody should have to read.
    /// </summary>
    private static readonly (string Key, string Name)[] Known =
    [
        ("bgss1", "Cliffs at sunset"),
        ("bgss2", "Keep gates"),
        ("bgss3", "Misty castle"),
        ("water", "Shoreline"),
        ("water2", "Still water"),
        ("snow1", "Snowy pines"),
        ("houseinside", "House interior"),
        ("houseinside2", "House cellar"),
        ("homer", "Homer in the snow"),
        ("blackblueleftknot", "Blue knot, left"),
        ("blackbluerightknot", "Blue knot, right"),
        ("blackgreyknotcenter", "Grey knot, centre"),
        ("blackgreyrightknot", "Grey knot, right"),
        ("blacktowhite", "Black to white"),
        ("blacktowhite2", "Black to white, knot left"),
        ("redtowhite", "Red to white"),
        ("redtowhite2", "Red to white, knot left"),
        ("pinktowhite", "Pink to white"),
        ("pinktowhite2", "Pink to white, knot left"),
        ("blue", "Blue"),
        ("green", "Green"),
        ("red", "Red"),
    ];

    private readonly Dictionary<string, SignaturePreset> _byKey = new(StringComparer.Ordinal);

    /// <param name="webRoot">
    /// Where the files are, which is wwwroot in the app and the same folder found
    /// from the test assembly in tests. A parameter rather than a constant for that
    /// reason.
    /// </param>
    public SignaturePresets(string webRoot, ILogger<SignaturePresets>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webRoot);

        var folder = Path.Combine(webRoot, "img", "sig");

        foreach (var (key, name) in Known)
        {
            // The extension is the file's, not the design's: two of these are jpg
            // and a design should not have to know which.
            var path = new[] { ".png", ".jpg", ".jpeg" }
                .Select(ext => Path.Combine(folder, key + ext))
                .FirstOrDefault(File.Exists);

            if (path is null)
            {
                log?.LogWarning("Signature preset {Key} is missing from {Folder}.", key, folder);
                continue;
            }

            _byKey[key] = new SignaturePreset(
                key,
                name,
                $"/img/sig/{Path.GetFileName(path)}",
                $"/img/sig/thumb/{key}.png");
        }

        All = Known
            .Select(k => _byKey.GetValueOrDefault(k.Key))
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

        Root = folder;
    }

    /// <summary>In picker order, and only the ones whose file is actually there.</summary>
    public IReadOnlyList<SignaturePreset> All { get; }

    public IReadOnlyCollection<string> Keys => _byKey.Keys;

    private string Root { get; }

    public SignaturePreset? Find(string? key) =>
        key is null ? null : _byKey.GetValueOrDefault(key);

    /// <summary>
    /// The bytes for a key, or null.
    ///
    /// Read per render rather than held in memory: renders happen on a daily pass,
    /// and 1.4MB of backgrounds resident for the life of the process to save a file
    /// read that happens twenty times a day is the wrong trade.
    /// </summary>
    public byte[]? Read(string? key)
    {
        if (Find(key) is not { } preset)
        {
            return null;
        }

        var path = Path.Combine(Root, Path.GetFileName(preset.Path));

        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

namespace Rmv.Web.Signature;

/// <summary>Where a text element measures from.</summary>
public enum TextAlign
{
    Left,
    Centre,
    Right,
}

public enum BackgroundKind
{
    /// <summary>A flat colour, which is what v1 called "default black".</summary>
    Colour,

    /// <summary>One of the presets shipped with the site.</summary>
    Preset,

    /// <summary>One the member uploaded.</summary>
    Upload,
}

/// <summary>
/// The numbers a signature refuses past, in one place.
///
/// Every one of them is a limit on what a member can make the server do, so they
/// are read by the model that clamps a design, by the form that edits one and by
/// the renderer. Same reason GalleryLimits and CharacterLimits exist.
/// </summary>
public static class SignatureLimits
{
    /// <summary>
    /// 520x160, which is what every v1 preset was and what a forum signature is.
    /// Fixed for now: a second size can follow when somebody asks.
    /// </summary>
    public const int Width = 520;

    public const int Height = 160;

    /// <summary>
    /// Twelve, because that is exactly what v1's grid of three columns by four rows
    /// could hold. Anyone wanting more than twelve lines in a signature has other
    /// problems.
    /// </summary>
    public const int MaxElements = 12;

    /// <summary>
    /// Long enough for the v1 default template with room to spare, short enough that
    /// twelve of them cannot become a wall of text the renderer has to measure.
    /// </summary>
    public const int MaxTemplate = 160;

    public const int MinFontSize = 8;

    public const int MaxFontSize = 48;

    /// <summary>How many backgrounds one member may keep. His number.</summary>
    public const int MaxBackgrounds = 2;
}

/// <summary>
/// One thing drawn on a signature.
/// </summary>
/// <param name="CharacterId">
/// Which character its tokens draw on, or null for a line about the member alone.
/// This is what replaces v1's %AC family: a second character is a second element,
/// rather than a token that loops every character onto its own line inside a fixed
/// grid.
/// </param>
/// <param name="Outline">
/// A one pixel outline, which is the difference between readable and not over a
/// screenshot background. Null for none.
/// </param>
public sealed record SignatureElement(
    int X,
    int Y,
    TextAlign Align,
    string Font,
    int Size,
    string Colour,
    string? Outline,
    int? CharacterId,
    string Template);

/// <summary>
/// A whole signature, before it is drawn.
///
/// A record rather than an entity: this is the shape the renderer and the editor
/// agree on, and it is what gets stored as JSON. Persistence comes later and does
/// not change it.
/// </summary>
public sealed record SignatureDesign(
    BackgroundKind Background,
    string? BackgroundKey,
    string Colour,
    IReadOnlyList<SignatureElement> Elements)
{
    /// <summary>
    /// What a member gets before they touch anything: v1's own default template,
    /// which is the line people recognise from ten years of forum posts.
    /// </summary>
    public static SignatureDesign Default(int? characterId) => new(
        BackgroundKind.Colour,
        null,
        "#0a0c12",
        [
            new SignatureElement(
                X: 12,
                Y: 18,
                Align: TextAlign.Left,
                Font: SignatureFonts.DefaultKey,
                Size: 22,
                Colour: "#e8d8a0",
                Outline: null,
                CharacterId: characterId,
                Template: "%Name%%SP%Level %Level% %Race% %Class%"),
            new SignatureElement(
                X: 12,
                Y: 48,
                Align: TextAlign.Left,
                Font: SignatureFonts.DefaultKey,
                Size: 17,
                Colour: "#c9c2b4",
                Outline: null,
                CharacterId: characterId,
                Template: "%Guild%%SP%%Rank%%SP%%Score%"),
            new SignatureElement(
                X: 12,
                Y: 134,
                Align: TextAlign.Left,
                Font: SignatureFonts.DefaultKey,
                Size: 14,
                Colour: "#a89f8c",
                Outline: null,
                CharacterId: null,
                Template: "%User% has played %AllChars% characters in %AllGames% games"),
        ]);
}

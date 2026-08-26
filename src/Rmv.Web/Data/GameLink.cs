using System.Diagnostics.CodeAnalysis;

namespace Rmv.Web.Data;

/// <summary>What a link points at, so the page can label and order them sensibly.</summary>
public enum GameLinkKind
{
    Herald,
    Guild,
    Character,
    Stats,
    Official,
    Other,
}

/// <summary>An external link belonging to one game.</summary>
public class GameLink
{
    public int Id { get; set; }

    public int GamePresenceId { get; set; }

    public GamePresence? Game { get; set; }

    public GameLinkKind Kind { get; set; }

    /// <summary>Button text. Falls back to the kind when blank.</summary>
    public string Label { get; set; } = "";

    public string Url { get; set; } = "";

    public int SortOrder { get; set; }

    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Kind.ToString() : Label;

    /// <summary>Host only, for the title attribute, so people can see where a button goes.</summary>
    public string? Host => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : null;
}

/// <summary>
/// Validates a URL before it is ever rendered as an href.
///
/// This is the one place the site puts operator-supplied text into an attribute
/// that the browser will act on. Razor escapes the value, which stops it breaking
/// out of the attribute, but escaping does nothing about the scheme:
/// href="javascript:alert(1)" is perfectly well-formed HTML and still executes.
/// So the scheme is checked against an allowlist rather than the value being
/// sanitised.
/// </summary>
public static class ExternalUrl
{
    public const int MaxLength = 400;

    public static bool TryParse(string? value, [NotNullWhen(true)] out string? normalised)
    {
        normalised = null;

        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength)
        {
            return false;
        }

        var trimmed = value.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // Allowlist, not a blocklist. javascript:, data:, vbscript: and file: all
        // execute or leak in some browser somewhere; only these two are ever a
        // link to another site.
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        normalised = uri.AbsoluteUri;
        return true;
    }

    public static bool IsValid(string? value) => TryParse(value, out _);
}

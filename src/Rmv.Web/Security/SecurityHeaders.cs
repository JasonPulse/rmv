namespace Rmv.Web.Security;

/// <summary>
/// The response headers that tell a browser what this site is allowed to do.
///
/// Written once, applied to everything, including static files and the error page.
/// A header set per page is a header missing from whichever page nobody thought
/// about, and the ones that matter most here are on the responses that are not
/// pages at all: a portrait or a screenshot is bytes a member uploaded, served
/// from our own origin, and nosniff is what stops a browser deciding one of them
/// is HTML after all.
///
/// This exists because a scanner asked for /admin/.aws/credentials.bak. It found
/// nothing, as every probe does here, but the answer to "are we exposing anything"
/// is not only "no file leaks". It is also that a browser cannot be talked into
/// running something on our behalf, which is what the content policy is for.
/// </summary>
public sealed class SecurityHeaders(RequestDelegate next, IConfiguration config)
{
    /// <summary>
    /// Discord's CDN, the only third party the site loads anything from, and only
    /// an avatar for a signed-in member. See DiscordUser.AvatarUrl.
    /// </summary>
    private const string DiscordCdn = "https://cdn.discordapp.com";

    private readonly string _policy = Policy(config);

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        // Set before the response starts, so it covers a 404 and a static file as
        // much as a page.
        headers["Content-Security-Policy"] = _policy;

        // The important one for the image endpoints. They echo a content type read
        // from the file's own bytes, and this stops a browser second-guessing it.
        headers["X-Content-Type-Options"] = "nosniff";

        // frame-ancestors in the policy above is the real control; this is for
        // anything old enough not to read it.
        headers["X-Frame-Options"] = "DENY";

        // A full URL is never sent to another site. The herald links on a character
        // card go to someone else's server, and which member's roster page somebody
        // was reading is nobody else's business.
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        // Nothing here wants a camera, a microphone or a location.
        headers["Permissions-Policy"] =
            "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), "
            + "microphone=(), payment=(), usb=()";

        return next(context);
    }

    /// <summary>
    /// The content policy, built from the same configuration the page reads.
    ///
    /// script-src has no 'unsafe-inline'. That is the whole point of it, and it is
    /// what forced the confirm dialogs out of onsubmit attributes and into
    /// wwwroot/js/confirm.js. Those attributes interpolated member-supplied names
    /// into JavaScript source, which Razor cannot make safe: it encodes the quote,
    /// the HTML parser decodes it again, and the script sees a closing quote. An
    /// alias of "');alert(1)//" ran in an admin's browser on /admin/members.
    ///
    /// style-src keeps 'unsafe-inline' because a handful of views set a margin or a
    /// custom property in a style attribute, and an attribute cannot carry a hash
    /// or a nonce. None of them interpolate anything a member typed; the only
    /// computed one is a bar height, which is a number.
    /// </summary>
    private static string Policy(IConfiguration config)
    {
        // Analytics is off unless both values are set, so its origin is only
        // allowed when the page will actually load it. Same configuration, one
        // decision; see _Layout.
        var umami = Origin(config["Analytics:UmamiScriptUrl"]);
        var extra = string.IsNullOrEmpty(umami) ? "" : " " + umami;

        return string.Join("; ",
        [
            "default-src 'self'",
            "base-uri 'self'",
            "frame-ancestors 'none'",
            "form-action 'self'",
            "object-src 'none'",
            $"img-src 'self' {DiscordCdn}",
            $"script-src 'self'{extra}",
            "style-src 'self' 'unsafe-inline'",
            "font-src 'self'",
            $"connect-src 'self'{extra}",
        ]);
    }

    /// <summary>
    /// Scheme and host of a configured URL, or empty. A policy takes an origin, and
    /// a misconfigured value must not become a source that allows everything.
    /// </summary>
    private static string Origin(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? $"{uri.Scheme}://{uri.Authority}"
            : "";
}

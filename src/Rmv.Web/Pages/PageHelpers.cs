using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Rmv.Web.Pages;

/// <summary>
/// The two things every page with a form ends up needing, kept out of the pages
/// themselves so there is one copy of each.
/// </summary>
public static class PageHelpers
{
    /// <summary>
    /// Reads a one-shot value a redirect put in the query string, or null.
    ///
    /// Redirect-after-post is how every form here reports what it did, so this
    /// idiom was written out ten times across three pages. The Count check is the
    /// part worth having once: an absent key yields an empty StringValues, not
    /// null, so a plain null test silently passes.
    /// </summary>
    public static string? Flash(this PageModel page, string key) =>
        page.Request.Query[key] is { Count: > 0 } value ? value.ToString() : null;

    /// <summary>
    /// Discards validation state for every bound model except the one whose form
    /// was actually posted.
    ///
    /// A page with two forms binds a model per form. A post from one carries none
    /// of the other's fields, so the other's [Required] and length rules fail and
    /// ModelState.IsValid comes back false for a handler with no interest in them.
    /// The save then bailed to Page() and looked like nothing had happened, because
    /// the messages rendered beside fields the operator was not looking at.
    ///
    /// Call this first in a handler, naming its own model.
    /// </summary>
    public static void ValidateOnly(this PageModel page, string prefix)
    {
        foreach (var key in page.ModelState.Keys
                     .Where(k => !k.StartsWith(prefix + ".", StringComparison.Ordinal))
                     .ToList())
        {
            page.ModelState.Remove(key);
        }
    }
}

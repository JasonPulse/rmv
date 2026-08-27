using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Rmv.Web.Pages;

public static class ModelStateScope
{
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

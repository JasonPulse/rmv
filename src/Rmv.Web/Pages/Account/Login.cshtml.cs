using AspNet.Security.OAuth.Discord;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Rmv.Web.Pages.Account;

public class LoginModel(SiteOptions site) : PageModel
{
    /// <summary>
    /// Hands off to Discord. There is no login form, so this page only ever
    /// redirects; it renders nothing.
    /// </summary>
    public IActionResult OnGet(string? returnUrl = null)
    {
        if (!site.DiscordEnabled)
        {
            return RedirectToPage("/Index");
        }

        // Only local return paths, so an attacker cannot bounce a member off-site.
        var target = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/";

        return Challenge(
            new AuthenticationProperties { RedirectUri = target },
            DiscordAuthenticationDefaults.AuthenticationScheme);
    }
}

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Rmv.Web.Pages.Account;

public class LogoutModel : PageModel
{
    // POST only: a GET logout can be triggered by any image tag on any page.
    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Index");
    }

    public IActionResult OnGet() => RedirectToPage("/Index");
}

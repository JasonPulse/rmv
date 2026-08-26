using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Rmv.Web.Pages.Account;

[Authorize]
public class ProfileModel : PageModel
{
    public string DisplayName => User.Identity?.Name ?? "unknown";

    public string? DiscordId =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
}

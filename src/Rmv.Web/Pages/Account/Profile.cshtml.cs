using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

namespace Rmv.Web.Pages.Account;

[Authorize]
public class ProfileModel(IServiceProvider services, IConfiguration config) : PageModel
{
    public string DisplayName => DiscordUser.Name(User);

    public string? DiscordId => DiscordUser.Id(User);

    public string? AvatarUrl => DiscordUser.AvatarUrl(User, 128);

    public Member? Record { get; private set; }

    public bool IsRoot { get; private set; }

    /// <summary>Root admins are not in the members table as approved, but they are.</summary>
    public bool CanContribute => IsRoot || Record is { Status: MemberStatus.Approved };

    public async Task OnGetAsync(CancellationToken ct)
    {
        IsRoot = AdminPolicy.IsRootAdmin(config, DiscordId);

        var db = services.GetService<RmvDbContext>();
        if (db is null || DiscordId is null)
        {
            return;
        }

        try
        {
            // Ensure rather than look up, so the profile is never the page that
            // tells you your account does not exist while you are signed in.
            var directory = services.GetRequiredService<MemberDirectory>();
            Record = await directory.EnsureAsync(User, ct);
        }
        catch
        {
            // The page still renders from claims alone; status just shows unknown.
        }
    }
}

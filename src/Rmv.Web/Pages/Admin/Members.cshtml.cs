using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

namespace Rmv.Web.Pages.Admin;

public class MembersModel(RmvDbContext db, IConfiguration config) : PageModel
{
    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public string? Notice { get; private set; }

    public string? Error { get; private set; }

    public record Row(Member Member, bool IsRoot, bool IsSelf);

    private string? MyId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostGrantAsync(int id, CancellationToken ct)
        => await SetAdminAsync(id, true, ct);

    public async Task<IActionResult> OnPostRevokeAsync(int id, CancellationToken ct)
        => await SetAdminAsync(id, false, ct);

    private async Task<IActionResult> SetAdminAsync(int id, bool grant, CancellationToken ct)
    {
        var member = await db.Members.FindAsync([id], ct);
        if (member is null)
        {
            return NotFound();
        }

        // Removing your own access is the one mistake that cannot be undone from
        // inside the app, so it is refused outright.
        if (!grant && member.DiscordId == MyId)
        {
            return RedirectToPage(new { error = "self" });
        }

        member.IsAdmin = grant;
        await db.SaveChangesAsync(ct);

        return RedirectToPage(new { granted = grant ? member.DisplayName : null,
                                    revoked = grant ? null : member.DisplayName });
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var members = await db.Members
            .OrderByDescending(m => m.IsAdmin).ThenByDescending(m => m.LastSeenAt)
            .AsNoTracking()
            .ToListAsync(ct);

        var me = MyId;
        Rows = members
            .Select(m => new Row(m, AdminPolicy.IsRootAdmin(config, m.DiscordId), m.DiscordId == me))
            .ToList();

        if (Request.Query["error"] == "self")
        {
            Error = "You cannot remove your own admin access. Ask another admin.";
        }

        if (Request.Query["granted"] is { Count: > 0 } g)
        {
            Notice = $"{g} is now an admin.";
        }

        if (Request.Query["revoked"] is { Count: > 0 } r)
        {
            Notice = $"{r} is no longer an admin.";
        }
    }
}

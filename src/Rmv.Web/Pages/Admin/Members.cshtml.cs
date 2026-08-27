using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

namespace Rmv.Web.Pages.Admin;

public class MembersModel(RmvDbContext db, IConfiguration config, CurrentMember me) : PageModel
{
    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public string? Notice { get; private set; }

    public string? Error { get; private set; }

    public record Row(Member Member, bool IsRoot, bool IsSelf);

    public int PendingCount { get; private set; }

    private string? MyId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostApproveAsync(int id, CancellationToken ct)
        => await SetStatusAsync(id, MemberStatus.Approved, ct);

    public async Task<IActionResult> OnPostBlockAsync(int id, CancellationToken ct)
        => await SetStatusAsync(id, MemberStatus.Blocked, ct);

    public async Task<IActionResult> OnPostGrantAsync(int id, CancellationToken ct)
        => await SetAdminAsync(id, true, ct);

    public async Task<IActionResult> OnPostRevokeAsync(int id, CancellationToken ct)
        => await SetAdminAsync(id, false, ct);

    private async Task<IActionResult> SetStatusAsync(int id, MemberStatus status, CancellationToken ct)
    {
        var member = await db.Members.FindAsync([id], ct);
        if (member is null)
        {
            return NotFound();
        }

        // Blocking yourself is the same trap as revoking your own admin.
        if (status == MemberStatus.Blocked && member.DiscordId == MyId)
        {
            return RedirectToPage(new { error = "self" });
        }

        member.Status = status;

        if (status == MemberStatus.Approved)
        {
            member.ApprovedAt = DateTimeOffset.UtcNow;
            member.ApprovedBy = await me.HandleAsync(User, ct);
        }
        else
        {
            // A blocked member loses admin too, so re-approving does not silently
            // hand back rights they had before.
            member.IsAdmin = false;
            member.ApprovedAt = null;
            member.ApprovedBy = null;
        }

        await db.SaveChangesAsync(ct);

        return RedirectToPage(status == MemberStatus.Approved
            ? new { approved = member.Handle }
            : new { blocked = member.Handle });
    }

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

        // Admin implies contributing, so promoting also approves. Otherwise an
        // admin could edit the site but not add a character, which is nonsense.
        if (grant && member.Status == MemberStatus.Pending)
        {
            member.Status = MemberStatus.Approved;
            member.ApprovedAt = DateTimeOffset.UtcNow;
            member.ApprovedBy = await me.HandleAsync(User, ct);
        }

        await db.SaveChangesAsync(ct);

        return RedirectToPage(new { granted = grant ? member.Handle : null,
                                    revoked = grant ? null : member.Handle });
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        // Pending first: the whole point of the page is that they are waiting.
        var members = await db.Members
            .OrderBy(m => m.Status == MemberStatus.Pending ? 0 : m.Status == MemberStatus.Approved ? 1 : 2)
            .ThenByDescending(m => m.IsAdmin)
            .ThenByDescending(m => m.LastSeenAt)
            .AsNoTracking()
            .ToListAsync(ct);

        PendingCount = members.Count(m => m.Status == MemberStatus.Pending);

        var me = MyId;
        Rows = members
            .Select(m => new Row(m, AdminPolicy.IsRootAdmin(config, m.DiscordId), m.DiscordId == me))
            .ToList();

        if (Request.Query["error"] == "self")
        {
            Error = "You cannot remove your own access. Ask another admin.";
        }

        if (Request.Query["approved"] is { Count: > 0 } ap)
        {
            Notice = $"{ap} is approved and can add characters.";
        }

        if (Request.Query["blocked"] is { Count: > 0 } bl)
        {
            Notice = $"{bl} is blocked.";
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

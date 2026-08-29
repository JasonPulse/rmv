using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;
using Rmv.Web.Gallery;

namespace Rmv.Web.Pages.Gallery;

/// <summary>
/// The screenshot gallery. Public to look at, approved members to add to.
///
/// Viewing is the point: this is the guild's own archive of twenty years, and the
/// reason to keep it here rather than in a Discord channel is that a channel
/// scrolls and an attachment ends up behind a login.
///
/// Uploading is rate limited. It is the one place an authenticated caller can make
/// the server do work proportional to what they send, and the bytes land in
/// Postgres.
/// </summary>
public class IndexModel(
    IServiceProvider services,
    GalleryService gallery,
    CurrentMember me) : PageModel
{
    public IReadOnlyList<Screenshot> Shots { get; private set; } = [];

    public int PageNumber { get; private set; } = 1;

    public int Pages { get; private set; } = 1;

    public int Mine { get; private set; }

    public bool CanUpload { get; private set; }

    public bool CanRemoveAny { get; private set; }

    public int? MemberId { get; private set; }

    public bool DatabaseUnavailable { get; private set; }

    public string? Error { get; private set; }

    public string? Notice { get; private set; }

    public Task OnGetAsync(int? page, CancellationToken ct) => LoadAsync(page, ct);

    /// <summary>
    /// Removes one, checked here rather than by an attribute.
    ///
    /// Razor Pages ignores [Authorize] on a handler method, and the compiler now
    /// refuses to let one be written, so this is the only kind of handler check that
    /// is actually a check. The service scopes the row to the caller, so an id
    /// belonging to someone else is not found rather than found and then refused.
    /// </summary>
    public async Task<IActionResult> OnPostRemoveAsync(int id, CancellationToken ct)
    {
        var member = await me.GetAsync(User, ct);
        if (member is null)
        {
            return Forbid();
        }

        var removed = await gallery.RemoveAsync(member, id, ct);

        return RedirectToPage(removed ? new { removed = true } : null);
    }

    private async Task LoadAsync(int? page, CancellationToken ct)
    {
        Notice = this.Flash("uploaded") is not null ? "Up it goes."
            : this.Flash("removed") is not null ? "Gone." : null;

        var member = await SafeMemberAsync(ct);
        MemberId = member?.Id;
        CanUpload = member?.CanContribute ?? false;
        CanRemoveAny = member?.CanAdminister ?? false;

        DatabaseUnavailable = !await this.TryLoadAsync(services, async db =>
        {
            // A blocked member is off the roster, so their screenshots go with them.
            var all = db.Screenshots
                .Where(s => s.Member != null && s.Member.Status != MemberStatus.Blocked);

            var total = await all.CountAsync(ct);
            Pages = Math.Max(1, (int)Math.Ceiling(total / (double)GalleryLimits.PageSize));
            PageNumber = Math.Clamp(page ?? 1, 1, Pages);

            Shots = await all
                .Include(s => s.Member)
                .Include(s => s.Game)
                .OrderByDescending(s => s.UploadedAt)
                .ThenByDescending(s => s.Id)
                .Skip((PageNumber - 1) * GalleryLimits.PageSize)
                .Take(GalleryLimits.PageSize)
                .AsNoTracking()
                .ToListAsync(ct);

            if (member is not null)
            {
                Mine = await db.Screenshots.CountAsync(s => s.MemberId == member.Id, ct);
            }
        });
    }

    /// <summary>
    /// The signed-in member, or null, without letting a database problem stop a
    /// public page rendering. CurrentMember.GetAsync propagates on purpose.
    /// </summary>
    private async Task<Member?> SafeMemberAsync(CancellationToken ct)
    {
        try
        {
            return await me.GetAsync(User, ct);
        }
        catch
        {
            return null;
        }
    }
}

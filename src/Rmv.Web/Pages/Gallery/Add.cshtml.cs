using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;
using Rmv.Web.Gallery;
using Rmv.Web.Tools;

namespace Rmv.Web.Pages.Gallery;

/// <summary>
/// Uploading a screenshot.
///
/// Its own page rather than a panel on the gallery, so the framework can gate it.
/// Razor Pages ignores [Authorize] and [EnableRateLimiting] on a handler method,
/// and putting them on the gallery's own class would gate and throttle looking at
/// it, which is the part that should be public and free. A separate page puts both
/// attributes where they are enforced and leaves viewing alone.
///
/// Rate limited because this is the one place an authenticated caller can make the
/// server do work proportional to what they send, and the bytes land in Postgres.
/// </summary>
[Authorize(Policy = MemberPolicy.Approved)]
[EnableRateLimiting(RateLimitPolicies.Upload)]
public class AddModel(RmvDbContext db, GalleryService gallery, CurrentMember me) : PageModel
{
    public IReadOnlyList<GamePresence> Games { get; private set; } = [];

    public int Mine { get; private set; }

    public string? Error { get; private set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required(ErrorMessage = "Pick a file.")]
        [Display(Name = "Screenshot")]
        public IFormFile? File { get; set; }

        [StringLength(GalleryLimits.MaxCaption)]
        public string? Caption { get; set; }

        [Display(Name = "Game")]
        public int? GamePresenceId { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var member = await me.GetAsync(User, ct);
        if (member is null)
        {
            return Forbid();
        }

        if (!ModelState.IsValid || Input.File is not { Length: > 0 } file)
        {
            Error = "Pick a file to upload.";
            await LoadAsync(ct);
            return Page();
        }

        await using var stream = file.OpenReadStream();

        // Nothing about the file other than its bytes is passed on. Not its name,
        // not its extension, not the content type it claims. See ImageProbe.
        var outcome = await gallery.AddAsync(
            member, stream, file.Length, Input.Caption, Input.GamePresenceId, ct);

        if (!outcome.Ok)
        {
            Error = outcome.Error;
            await LoadAsync(ct);
            return Page();
        }

        return RedirectToPage("/Gallery/Index", new { uploaded = true });
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Games = await db.GamePresences
            .Listed()
            .AsNoTracking()
            .ToListAsync(ct);

        if (await me.GetAsync(User, ct) is { } member)
        {
            Mine = await db.Screenshots.CountAsync(s => s.MemberId == member.Id, ct);
        }
    }
}

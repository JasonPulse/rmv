using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

namespace Rmv.Web.Pages.Admin;

/// <summary>
/// Editor for the "where we've been" list.
///
/// Authorisation is applied by convention in Program.cs: AuthorizeFolder on
/// /Admin with the Admin policy, so being signed in is not enough. Open in
/// Development so it can be used before Discord exists.
/// </summary>
public class HistoryModel(RmvDbContext db) : PageModel
{
    public IReadOnlyList<GamePresence> Rows { get; private set; } = [];

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public LinkInputModel LinkInput { get; set; } = new();

    public string? Notice { get; private set; }

    public class InputModel
    {
        public int? Id { get; set; }

        [Required, StringLength(80)]
        [Display(Name = "Game")]
        public string Game { get; set; } = "";

        [Required, StringLength(240)]
        [Display(Name = "Guild tags")]
        public string Guilds { get; set; } = "";

        [StringLength(40)]
        public string? Period { get; set; }

        public bool IsActive { get; set; }

        [Range(0, 999)]
        public int SortOrder { get; set; }
    }

    public class LinkInputModel
    {
        public int? Id { get; set; }

        [Required, Display(Name = "Game")]
        public int GamePresenceId { get; set; }

        public GameLinkKind Kind { get; set; } = GameLinkKind.Herald;

        [Required, StringLength(60)]
        public string Label { get; set; } = "";

        [Required, StringLength(ExternalUrl.MaxLength)]
        public string Url { get; set; } = "";

        [Range(0, 999)]
        public int SortOrder { get; set; }
    }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostSaveLinkAsync(CancellationToken ct)
    {
        // The scheme allowlist, not just a length check. See ExternalUrl.
        if (!ExternalUrl.TryParse(LinkInput.Url, out var url))
        {
            ModelState.AddModelError("LinkInput.Url", "Must be an absolute http or https URL.");
        }

        if (!await db.GamePresences.AnyAsync(g => g.Id == LinkInput.GamePresenceId, ct))
        {
            ModelState.AddModelError("LinkInput.GamePresenceId", "Pick a game.");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(ct);
            return Page();
        }

        if (LinkInput.Id is { } id)
        {
            var row = await db.GameLinks.FindAsync([id], ct);
            if (row is null)
            {
                return NotFound();
            }

            row.GamePresenceId = LinkInput.GamePresenceId;
            row.Kind = LinkInput.Kind;
            row.Label = LinkInput.Label.Trim();
            row.Url = url!;
            row.SortOrder = LinkInput.SortOrder;
        }
        else
        {
            db.GameLinks.Add(new GameLink
            {
                GamePresenceId = LinkInput.GamePresenceId,
                Kind = LinkInput.Kind,
                Label = LinkInput.Label.Trim(),
                Url = url!,
                SortOrder = LinkInput.SortOrder,
            });
        }

        await db.SaveChangesAsync(ct);
        return RedirectToPage(new { saved = true });
    }

    public async Task<IActionResult> OnPostDeleteLinkAsync(int id, CancellationToken ct)
    {
        var row = await db.GameLinks.FindAsync([id], ct);
        if (row is not null)
        {
            db.GameLinks.Remove(row);
            await db.SaveChangesAsync(ct);
        }

        return RedirectToPage(new { deleted = true });
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync(ct);
            return Page();
        }

        if (Input.Id is { } id)
        {
            var row = await db.GamePresences.FindAsync([id], ct);
            if (row is null)
            {
                return NotFound();
            }

            row.Game = Input.Game.Trim();
            row.Guilds = Input.Guilds.Trim();
            row.Period = string.IsNullOrWhiteSpace(Input.Period) ? null : Input.Period.Trim();
            row.IsActive = Input.IsActive;
            row.SortOrder = Input.SortOrder;
        }
        else
        {
            db.GamePresences.Add(new GamePresence
            {
                Game = Input.Game.Trim(),
                Guilds = Input.Guilds.Trim(),
                Period = string.IsNullOrWhiteSpace(Input.Period) ? null : Input.Period.Trim(),
                IsActive = Input.IsActive,
                SortOrder = Input.SortOrder,
            });
        }

        await db.SaveChangesAsync(ct);

        // Redirect after post, so a refresh does not resubmit.
        return RedirectToPage(new { saved = true });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken ct)
    {
        var row = await db.GamePresences.FindAsync([id], ct);
        if (row is not null)
        {
            db.GamePresences.Remove(row);
            await db.SaveChangesAsync(ct);
        }

        return RedirectToPage(new { deleted = true });
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Rows = await db.GamePresences
            .Include(g => g.Links.OrderBy(l => l.SortOrder).ThenBy(l => l.Label))
            .OrderByDescending(g => g.IsActive).ThenBy(g => g.SortOrder).ThenBy(g => g.Game)
            .AsNoTracking()
            .ToListAsync(ct);

        if (Request.Query.ContainsKey("saved")) Notice = "Saved.";
        if (Request.Query.ContainsKey("deleted")) Notice = "Deleted.";
    }
}

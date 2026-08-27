using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;
using Rmv.Web.Herald;

namespace Rmv.Web.Pages.Admin;

/// <summary>
/// Editor for the "where we've been" list.
///
/// Authorisation is applied by convention in Program.cs: AuthorizeFolder on
/// /Admin with the Admin policy, so being signed in is not enough. Open in
/// Development so it can be used before Discord exists.
/// </summary>
public class HistoryModel(RmvDbContext db, HeraldRegistry heralds) : PageModel
{
    public IReadOnlyList<GamePresence> Rows { get; private set; } = [];

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public LinkInputModel LinkInput { get; set; } = new();

    public string? Notice { get; private set; }

    /// <summary>Adapters available to point a game at.</summary>
    public IReadOnlyCollection<IHeraldAdapter> Adapters => heralds.All;

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

        /// <summary>Blank means characters cannot be added for this game.</summary>
        [Display(Name = "Herald adapter")]
        public string? HeraldAdapterKey { get; set; }

        [StringLength(ExternalUrl.MaxLength)]
        [Display(Name = "Herald base URL")]
        public string? HeraldBaseUrl { get; set; }
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

    /// <summary>
    /// Discards validation state for every bound model except the one whose form
    /// was actually posted.
    ///
    /// Two forms post to this page and each binds its own model. A post from the
    /// games form carries no LinkInput fields, so LinkInput's [Required]
    /// properties fail and ModelState.IsValid is false for a handler that has no
    /// interest in them. The save then bailed to Page() and looked like nothing
    /// had happened, because the messages rendered beside the link fields.
    /// </summary>
    private void ValidateOnly(string prefix)
    {
        foreach (var key in ModelState.Keys
                     .Where(k => !k.StartsWith(prefix + ".", StringComparison.Ordinal))
                     .ToList())
        {
            ModelState.Remove(key);
        }
    }

    public async Task<IActionResult> OnPostSaveLinkAsync(CancellationToken ct)
    {
        ValidateOnly(nameof(LinkInput));

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
        ValidateOnly(nameof(Input));

        var adapterKey = string.IsNullOrWhiteSpace(Input.HeraldAdapterKey) ? null : Input.HeraldAdapterKey.Trim();
        string? heraldUrl = null;

        var urlGiven = !string.IsNullOrWhiteSpace(Input.HeraldBaseUrl);

        if (adapterKey is not null)
        {
            if (heralds.Find(adapterKey) is null)
            {
                ModelState.AddModelError("Input.HeraldAdapterKey", "Unknown herald adapter.");
            }

            // Same scheme allowlist as the link URLs. The fetcher will also refuse
            // a private address at connect time unless it is allowlisted.
            if (!ExternalUrl.TryParse(Input.HeraldBaseUrl, out heraldUrl))
            {
                ModelState.AddModelError("Input.HeraldBaseUrl",
                    "Give an absolute http or https URL, e.g. https://herald.example.com");
            }
        }
        else if (urlGiven)
        {
            // Half-configured is the trap. Silently discarding the URL because no
            // adapter was chosen looks exactly like a save that worked, and then
            // the game never appears on /characters.
            ModelState.AddModelError("Input.HeraldAdapterKey",
                "Choose a herald adapter as well, or clear the URL. A URL on its own does nothing.");
        }

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
            row.HeraldAdapterKey = adapterKey;
            row.HeraldBaseUrl = adapterKey is null ? null : heraldUrl;
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
                HeraldAdapterKey = adapterKey,
                HeraldBaseUrl = adapterKey is null ? null : heraldUrl,
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

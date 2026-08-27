using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;
using Rmv.Web.Herald;
using Rmv.Web.Tools;

namespace Rmv.Web.Pages.Characters;

/// <summary>
/// Where a member adds and sees their own characters.
///
/// Requires the approved policy, not merely a sign-in: that is the whole point of
/// the approval gate. Rate limited because adding fetches from someone else's
/// herald.
/// </summary>
[Authorize(Policy = MemberPolicy.Approved)]
[EnableRateLimiting(RateLimitPolicies.Herald)]
public class IndexModel(
    RmvDbContext db,
    CharacterService characters,
    HeraldRegistry heralds,
    MemberDirectory members) : PageModel
{
    public IReadOnlyList<Character> Mine { get; private set; } = [];

    public IReadOnlyList<GamePresence> Games { get; private set; } = [];

    public string? Error { get; private set; }

    public string? Notice { get; private set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required(ErrorMessage = "Pick a game.")]
        [Display(Name = "Game")]
        public int GamePresenceId { get; set; }

        [Required(ErrorMessage = "Enter the character's name.")]
        [StringLength(32, MinimumLength = 2)]
        public string Name { get; set; } = "";
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(CancellationToken ct)
    {
        var member = await CurrentMemberAsync(ct);
        if (member is null)
        {
            // Only reachable without a Discord id on the principal, which should
            // not happen for an authenticated caller.
            Error = "Could not identify your account. Sign out and back in.";
            await LoadAsync(ct);
            return Page();
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(ct);
            return Page();
        }

        var outcome = await characters.AddAsync(member, Input.GamePresenceId, Input.Name, ct);

        if (!outcome.Ok)
        {
            Error = outcome.Error;
            await LoadAsync(ct);
            return Page();
        }

        return RedirectToPage(new { added = outcome.Character!.Name });
    }

    public async Task<IActionResult> OnPostRemoveAsync(int id, CancellationToken ct)
    {
        var member = await CurrentMemberAsync(ct);
        if (member is null)
        {
            return Forbid();
        }

        // Scoped to the caller's own rows, so an id from someone else's page
        // simply is not found.
        var character = await db.Characters
            .FirstOrDefaultAsync(c => c.Id == id && c.MemberId == member.Id, ct);

        if (character is not null)
        {
            db.Characters.Remove(character);
            await db.SaveChangesAsync(ct);
            return RedirectToPage(new { removed = character.Name });
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRefreshAsync(int id, CancellationToken ct)
    {
        var member = await CurrentMemberAsync(ct);
        if (member is null)
        {
            return Forbid();
        }

        var character = await db.Characters
            .Include(c => c.Game)
            .FirstOrDefaultAsync(c => c.Id == id && c.MemberId == member.Id, ct);

        if (character is null)
        {
            return RedirectToPage();
        }

        var ok = await characters.RefreshAsync(character, ct);
        await db.SaveChangesAsync(ct);

        return RedirectToPage(ok
            ? new { refreshed = character.Name }
            : new { failed = character.Name });
    }

    // Creates the row if it is missing, so a valid session from before the
    // sign-in hook existed does not dead-end on "your member record is missing".
    private Task<Member?> CurrentMemberAsync(CancellationToken ct) =>
        members.EnsureAsync(User, ct);

    private async Task LoadAsync(CancellationToken ct)
    {
        var member = await CurrentMemberAsync(ct);

        Mine = member is null
            ? []
            : await db.Characters
                .Include(c => c.Game)
                .Where(c => c.MemberId == member.Id)
                .OrderBy(c => c.Game!.Game).ThenBy(c => c.Name)
                .AsNoTracking()
                .ToListAsync(ct);

        // Only games that can actually look a character up: an adapter chosen, and
        // that adapter still registered. Offering the rest would just produce a
        // failure after the fact.
        var all = await db.GamePresences
            .OrderByDescending(g => g.IsActive).ThenBy(g => g.Game)
            .AsNoTracking()
            .ToListAsync(ct);

        Games = all.Where(g => heralds.Find(g.HeraldAdapterKey) is not null).ToList();

        if (Request.Query["added"] is { Count: > 0 } a) Notice = $"Added {a}.";
        if (Request.Query["removed"] is { Count: > 0 } r) Notice = $"Removed {r}.";
        if (Request.Query["refreshed"] is { Count: > 0 } f) Notice = $"Refreshed {f}.";
        if (Request.Query["failed"] is { Count: > 0 } x) Error = $"Could not refresh {x}. The herald may be down.";
    }
}

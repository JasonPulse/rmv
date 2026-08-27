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
    CurrentMember me) : PageModel
{
    public IReadOnlyList<Character> Mine { get; private set; } = [];

    public IReadOnlyList<GamePresence> Games { get; private set; } = [];

    /// <summary>
    /// Ids of the games that can look a character up. Drives which fields the add
    /// form shows, and the server decides the same way, so it does not matter
    /// whether the browser ran the script.
    /// </summary>
    public HashSet<int> HeraldGameIds { get; private set; } = [];

    public bool HasHerald(GamePresence game) => HeraldGameIds.Contains(game.Id);

    public string? Error { get; private set; }

    public string? Notice { get; private set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>
    /// Bound separately from Input so the two forms do not invalidate each other.
    /// Posting the add form carries no Edit fields, and a shared model's [Required]
    /// would fail on the fields the other form never sent.
    /// </summary>
    [BindProperty]
    public EditModel Edit { get; set; } = new();

    public class EditModel : SheetInput
    {
        // The stored name, so this is the column's own limit rather than the add
        // form's allowance for a pasted URL.
        [StringLength(32, MinimumLength = 2)]
        public string Name { get; set; } = "";
    }

    /// <summary>
    /// The stats a member types for a game with no herald.
    ///
    /// Neither is [Required]: whether they apply depends on the game picked, and a
    /// blanket attribute would block every herald add with a message about a field
    /// that was never shown. Both may be blank because plenty of these characters
    /// are a name and nothing else.
    ///
    /// A base class rather than a copy in each model. The add form and the per-card
    /// edit form take the same two fields with the same limits, and the limits have
    /// to match what CharacterService enforces.
    /// </summary>
    public abstract class SheetInput
    {
        [StringLength(60)]
        [Display(Name = "Job or class")]
        public string? Class { get; set; }

        [Range(1, 999)]
        public int? Level { get; set; }
    }

    public class InputModel : SheetInput
    {
        [Required(ErrorMessage = "Pick a game.")]
        [Display(Name = "Game")]
        public int GamePresenceId { get; set; }

        /// <summary>
        /// Longer than a name allows, because some heralds take a pasted character
        /// URL instead. The adapter decides what its own server accepts.
        /// </summary>
        [Required(ErrorMessage = "Enter the character's name.")]
        [StringLength(200, MinimumLength = 2)]
        public string Name { get; set; } = "";
    }

    /// <summary>
    /// Re-renders the page, optionally saying what went wrong.
    ///
    /// Six paths ended in the same three lines. LoadAsync is the part worth having
    /// once: forget it and the page comes back with an empty game list and no
    /// characters, which reads as the site having lost your data rather than as a
    /// rejected form.
    /// </summary>
    private async Task<IActionResult> RedisplayAsync(CancellationToken ct, string? error = null)
    {
        if (error is not null)
        {
            Error = error;
        }

        await LoadAsync(ct);
        return Page();
    }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RedisplayAsync(ct);

    public async Task<IActionResult> OnPostAddAsync(CancellationToken ct)
    {
        var member = await me.GetAsync(User, ct);
        if (member is null)
        {
            // Only reachable without a Discord id on the principal, which should
            // not happen for an authenticated caller.
            return await RedisplayAsync(ct, "Could not identify your account. Sign out and back in.");
        }

        this.ValidateOnly(nameof(Input));

        if (!ModelState.IsValid)
        {
            return await RedisplayAsync(ct);
        }

        // The game decides which path this is, not a radio button. A game either
        // has a herald to ask or it does not, and letting the member choose would
        // only let them choose wrong.
        var game = await db.GamePresences
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == Input.GamePresenceId, ct);

        var outcome = heralds.Find(game?.HeraldAdapterKey) is not null
            ? await characters.AddAsync(member, Input.GamePresenceId, Input.Name, ct)
            : await characters.AddManualAsync(
                member, Input.GamePresenceId, Input.Name, Input.Class, Input.Level, ct);

        if (!outcome.Ok)
        {
            return await RedisplayAsync(ct, outcome.Error);
        }

        return RedirectToPage(new { added = outcome.Character!.Name });
    }

    /// <summary>
    /// Corrects a hand-typed sheet. Nothing refreshes a manual character, so
    /// without this a typo would mean removing it and adding it again.
    /// </summary>
    public async Task<IActionResult> OnPostEditAsync(int id, CancellationToken ct)
    {
        var (member, character) = await MineAsync(id, ct);
        if (member is null) return Forbid();
        if (character is null) return RedirectToPage();

        this.ValidateOnly(nameof(Edit));

        if (!ModelState.IsValid)
        {
            return await RedisplayAsync(ct);
        }

        var outcome = await characters.UpdateManualAsync(
            character, Edit.Name, Edit.Class, Edit.Level, ct);

        if (!outcome.Ok)
        {
            return await RedisplayAsync(ct, outcome.Error);
        }

        return RedirectToPage(new { saved = character.Name });
    }

    public async Task<IActionResult> OnPostRemoveAsync(int id, CancellationToken ct)
    {
        var (member, character) = await MineAsync(id, ct);
        if (member is null) return Forbid();
        if (character is null) return RedirectToPage();

        db.Characters.Remove(character);
        await db.SaveChangesAsync(ct);

        return RedirectToPage(new { removed = character.Name });
    }

    public async Task<IActionResult> OnPostRefreshAsync(int id, CancellationToken ct)
    {
        var (member, character) = await MineAsync(id, ct);
        if (member is null) return Forbid();
        if (character is null) return RedirectToPage();

        var ok = await characters.RefreshAsync(character, ct);
        await db.SaveChangesAsync(ct);

        return RedirectToPage(ok
            ? new { refreshed = character.Name }
            : new { failed = character.Name });
    }

    /// <summary>
    /// One of the caller's own characters, with the member that owns it.
    ///
    /// Three handlers had their own copy of this: resolve the member or Forbid,
    /// then find the character scoped to that member's rows. The scoping is the
    /// authorisation check as much as the lookup, so an id from someone else's
    /// page simply is not found, and that is not a thing to keep three copies of.
    ///
    /// CurrentMember creates the row if it is missing, so a valid session from
    /// before the sign-in hook existed does not dead-end on "your member record is
    /// missing".
    /// </summary>
    private async Task<(Member? Member, Character? Character)> MineAsync(
        int id, CancellationToken ct)
    {
        var member = await me.GetAsync(User, ct);

        return member is null
            ? (null, null)
            : (member, await db.Characters
                .Include(c => c.Game)
                .FirstOrDefaultAsync(c => c.Id == id && c.MemberId == member.Id, ct));
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var member = await me.GetAsync(User, ct);

        Mine = member is null
            ? []
            : await db.Characters
                .Include(c => c.Game)
                .Where(c => c.MemberId == member.Id)
                .OrderBy(c => c.Game!.Game).ThenBy(c => c.Name)
                .AsNoTracking()
                .ToListAsync(ct);

        // Every game, not only the ones with a herald. Most of the servers the
        // guild has been through never had one, or no longer run it, and those
        // characters are the whole point of the list; a game with no herald takes
        // a sheet the member types instead.
        Games = await db.GamePresences
            .OrderByDescending(g => g.IsActive).ThenBy(g => g.Game)
            .AsNoTracking()
            .ToListAsync(ct);

        HeraldGameIds = Games
            .Where(g => heralds.Find(g.HeraldAdapterKey) is not null)
            .Select(g => g.Id)
            .ToHashSet();

        if (this.Flash("added") is { } a) Notice = $"Added {a}.";
        if (this.Flash("removed") is { } r) Notice = $"Removed {r}.";
        if (this.Flash("refreshed") is { } f) Notice = $"Refreshed {f}.";
        if (this.Flash("saved") is { } s) Notice = $"Saved {s}.";
        if (this.Flash("failed") is { } x) Error = $"Could not refresh {x}. The herald may be down.";
    }
}

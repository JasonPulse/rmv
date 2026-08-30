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

    /// <summary>
    /// Games whose herald admits it does not list everyone, and what it says about
    /// that. Only these offer the choice of typing the sheet in instead.
    ///
    /// The Armory is the case: it shows characters on a subscribed account only,
    /// and answers a lapsed one exactly as it answers a misspelling. Both the note
    /// and the offer come from IHeraldAdapter.CoverageNote, so the form cannot
    /// promise a choice the server then refuses.
    /// </summary>
    public IReadOnlyDictionary<int, string> HeraldNotes { get; private set; } =
        new Dictionary<int, string>();

    public bool HasHerald(GamePresence game) => HeraldGameIds.Contains(game.Id);

    public string? NoteFor(GamePresence game) =>
        HeraldNotes.TryGetValue(game.Id, out var note) ? note : null;

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
        [StringLength(CharacterLimits.MaxName, MinimumLength = CharacterLimits.MinName)]
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
    /// edit form take the same two fields, and both read their limits from
    /// CharacterLimits, which is what CharacterService enforces.
    /// </summary>
    public abstract class SheetInput
    {
        [StringLength(CharacterLimits.MaxClass)]
        [Display(Name = "Job or class")]
        public string? Class { get; set; }

        [Range(CharacterLimits.MinLevel, CharacterLimits.MaxLevel)]
        public int? Level { get; set; }
    }

    public class InputModel : SheetInput
    {
        [Required(ErrorMessage = "Pick a game.")]
        [Display(Name = "Game")]
        public int GamePresenceId { get; set; }

        /// <summary>
        /// Ticked by default: most members are subscribed and a looked-up sheet is
        /// better than a typed one. Only consulted for a game whose herald has a
        /// CoverageNote; see CharacterService.AddAsync.
        /// </summary>
        [Display(Name = "Look this character up")]
        public bool UseHerald { get; set; } = true;

        /// <summary>
        /// Longer than a name allows, because some heralds take a pasted character
        /// URL instead. The adapter decides what its own server accepts.
        /// </summary>
        [Required(ErrorMessage = "Enter the character's name.")]
        [StringLength(CharacterLimits.MaxTyped, MinimumLength = CharacterLimits.MinName)]
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

        // The page passes on what was asked for and does not decide anything. Which
        // path this takes is CharacterService.AddAsync's decision, from the game,
        // its adapter and the member's choice, in that order.
        var outcome = await characters.AddAsync(
            member,
            new CharacterRequest(
                Input.GamePresenceId, Input.Name, Input.Class, Input.Level, Input.UseHerald),
            ct);

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
            .Listed()
            .AsNoTracking()
            .ToListAsync(ct);

        var adapters = Games
            .Select(g => (Game: g, Adapter: heralds.Find(g.HeraldAdapterKey)))
            .Where(x => x.Adapter is not null)
            .ToList();

        HeraldGameIds = adapters.Select(x => x.Game.Id).ToHashSet();

        HeraldNotes = adapters
            .Where(x => x.Adapter!.CoverageNote is not null)
            .ToDictionary(x => x.Game.Id, x => x.Adapter!.CoverageNote!);

        if (this.Flash("added") is { } a) Notice = $"Added {a}.";
        if (this.Flash("removed") is { } r) Notice = $"Removed {r}.";
        if (this.Flash("refreshed") is { } f) Notice = $"Refreshed {f}.";
        if (this.Flash("saved") is { } s) Notice = $"Saved {s}.";
        if (this.Flash("failed") is { } x) Error = $"Could not refresh {x}. The herald may be down.";
    }
}

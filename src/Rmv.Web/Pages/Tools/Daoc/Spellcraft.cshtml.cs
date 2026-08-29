using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Rmv.Web.Data;
using Rmv.Web.Tools.Spellcraft;

namespace Rmv.Web.Pages.Tools.Daoc;

/// <summary>
/// Picks gems for an item slot and reports the bonuses, the caps breached, the
/// imbue points spent and the crafting skill needed.
///
/// Approved members only, the whole tool. Not the roll parser's model: that one is
/// deliberately anonymous, and this one was asked for as members only.
///
/// The class-level [Authorize] is the guard. The handlers also ask the approved
/// policy before writing, which is not redundant: Razor Pages ignores [Authorize]
/// on a handler method, so a handler that checks for itself is the only kind that
/// is actually checked.
///
/// The store and CurrentMember are resolved from the container rather than
/// injected, so the calculator still renders where neither exists. The arithmetic
/// does not need a database; only saving a template does.
/// </summary>
[Authorize(Policy = MemberPolicy.Approved)]
public class SpellcraftModel(
    SpellcraftTables tables,
    IServiceProvider services) : PageModel
{
    public SpellcraftTables Tables => tables;

    /// <summary>False for the sample set, which is what puts the warning on the page.</summary>
    public bool Verified => tables.Verified;

    public SpellcraftReport? Report { get; private set; }

    public IReadOnlyList<SpellcraftTemplate> Templates { get; private set; } = [];

    /// <summary>The slot the form is currently drawing sockets for, or null.</summary>
    public ItemSlot? Slot { get; private set; }

    public Realm? Realm { get; private set; }

    /// <summary>Gems this realm may put in this slot, grouped for the picker.</summary>
    public IReadOnlyList<Gem> Choices { get; private set; } = [];

    public string? Error { get; private set; }

    public string? Notice { get; private set; }

    /// <summary>True once the visitor is signed in and approved, so the save form shows.</summary>
    public bool CanSave { get; private set; }

    /// <summary>True when saving needs a template chosen to overwrite.</summary>
    public bool MustOverwrite { get; private set; }

    public bool AtCap => Templates.Count >= SpellcraftTemplate.MaxPerMember;

    [BindProperty]
    public DesignInput Design { get; set; } = new();

    /// <summary>
    /// Bound apart from Design so the two forms do not invalidate each other. The
    /// calculate post carries no template name, and a shared model's [Required]
    /// would fail on a field that form never sends. See PageHelpers.ValidateOnly.
    /// </summary>
    [BindProperty]
    public SaveInput Save { get; set; } = new();

    public class DesignInput
    {
        public string Realm { get; set; } = "";

        public string Slot { get; set; } = "";

        public int Level { get; set; }

        /// <summary>One entry per socket, blank for an empty one.</summary>
        public List<string> Gems { get; set; } = [];

        public SpellcraftDesign ToDesign() =>
            new(Realm ?? "", Slot ?? "", Level, Gems ?? []);

        public static DesignInput From(SpellcraftDesign design) => new()
        {
            Realm = design.RealmCode,
            Slot = design.SlotCode,
            Level = design.ItemLevel,
            Gems = [.. design.GemCodes],
        };
    }

    public class SaveInput
    {
        [Required(ErrorMessage = "Give the template a name.")]
        [StringLength(SpellcraftTemplate.MaxNameLength, MinimumLength = 1)]
        public string Name { get; set; } = "";

        /// <summary>The template to replace, when the member is at the cap.</summary>
        public int? OverwriteId { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int? load, CancellationToken ct)
    {
        await LoadAsync(ct);

        if (load is { } id && await MyTemplateAsync(id, ct) is { } template)
        {
            if (SpellcraftDesign.TryDecode(template.Design, out var decoded))
            {
                Design = DesignInput.From(decoded);
                Save.Name = template.Name;
                Save.OverwriteId = template.Id;
            }
            else
            {
                // A row written by an older or newer encoding. Better to say so
                // than to silently show an empty item.
                Error = $"\"{template.Name}\" was saved in a format this page no longer reads.";
            }
        }
        else if (Design.Level == 0)
        {
            Design.Level = tables.MaxItemLevel;
        }

        Evaluate();
        return Page();
    }

    public async Task<IActionResult> OnPostCalculateAsync(CancellationToken ct)
    {
        this.ValidateOnly(nameof(Design));
        await LoadAsync(ct);
        Evaluate();

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        var (member, denied) = await ApprovedMemberAsync(ct);
        if (denied is not null)
        {
            return denied;
        }

        this.ValidateOnly(nameof(Save));
        await LoadAsync(ct);
        Evaluate();

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Refuses to save something the calculator could not read, so a template
        // always loads back into a working item.
        if (Report is null)
        {
            Error ??= "Fix the item before saving it.";
            return Page();
        }

        var store = services.GetRequiredService<SpellcraftTemplateStore>();
        var outcome = await store.SaveAsync(
            member!.Id, Save.Name, Design.ToDesign(), Save.OverwriteId, ct);

        if (!outcome.Ok)
        {
            Error = outcome.Error;
            MustOverwrite = outcome.NeedsOverwrite;
            return Page();
        }

        return RedirectToPage(new { load = outcome.Template!.Id, saved = outcome.Template.Name });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken ct)
    {
        var (member, denied) = await ApprovedMemberAsync(ct);
        if (denied is not null)
        {
            return denied;
        }

        var store = services.GetRequiredService<SpellcraftTemplateStore>();
        var removed = await store.DeleteAsync(member!.Id, id, ct);

        // A template that is not theirs and one that does not exist give the same
        // answer, which is what stops this page confirming what somebody else owns.
        return removed is null
            ? RedirectToPage()
            : RedirectToPage(new { deleted = removed.Name });
    }

    /// <summary>
    /// The caller, if they are an approved member, or the result to return instead.
    ///
    /// Checked against the policy rather than against the cookie: signing in with
    /// Discord proves you have a Discord account and nothing more. Anonymous
    /// callers are challenged so they land on sign-in; signed-in but unapproved
    /// ones are forbidden, because signing in again will not help them.
    /// </summary>
    private async Task<(Member? Member, IActionResult? Denied)> ApprovedMemberAsync(CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return (null, Challenge());
        }

        var me = services.GetService<CurrentMember>();
        var access = me is null ? Access.None : await me.AccessAsync(User, ct);

        if (!access.CanContribute)
        {
            return (null, Forbid());
        }

        // Reachable for a root admin whose row could not be read, since their access
        // does not depend on one. A template has to belong to a member row.
        return access.Member is null ? (null, Forbid()) : (access.Member, null);
    }

    private Task<SpellcraftTemplate?> MyTemplateAsync(int id, CancellationToken ct)
    {
        var store = services.GetService<SpellcraftTemplateStore>();
        var member = MemberId;

        return store is null || member is null
            ? Task.FromResult<SpellcraftTemplate?>(null)
            : store.FindAsync(member.Value, id, ct);
    }

    private int? MemberId { get; set; }

    /// <summary>
    /// Everything the page needs whichever handler ran: who is asking, their
    /// templates, and the flash a redirect left behind.
    ///
    /// Five paths ended in the same calls. Missing it leaves the template list
    /// empty, which reads as the site having lost them rather than as a rejected
    /// form.
    /// </summary>
    private async Task LoadAsync(CancellationToken ct)
    {
        var store = services.GetService<SpellcraftTemplateStore>();
        var me = services.GetService<CurrentMember>();

        if (store is not null && me is not null && User.Identity?.IsAuthenticated == true)
        {
            var access = await me.AccessAsync(User, ct);
            CanSave = access.CanContribute;

            if (CanSave && access.Member is { } member)
            {
                MemberId = member.Id;
                Templates = await store.ListAsync(member.Id, ct);
            }
        }

        if (this.Flash("saved") is { } s) Notice = $"Saved {s}.";
        if (this.Flash("deleted") is { } d) Notice = $"Deleted {d}.";
    }

    /// <summary>
    /// Resolves the form against the tables and runs the arithmetic. Sets up the
    /// gem picker for whichever slot is now selected, so changing the slot redraws
    /// the sockets on the next post without any script.
    /// </summary>
    private void Evaluate()
    {
        Realm = tables.FindRealm(Design.Realm);
        Slot = tables.FindSlot(Design.Slot);

        if (Slot is not null)
        {
            Choices = tables.GemsFor(Realm, Slot);
        }

        if (string.IsNullOrEmpty(Design.Slot))
        {
            return;
        }

        var resolved = tables.Resolve(Design.ToDesign());
        if (resolved.Design is null)
        {
            Error ??= resolved.Error;
            return;
        }

        Report = SpellcraftCalculator.Evaluate(resolved.Design);

        // Normalised to the slot's socket count, so the form redraws with exactly
        // the inputs the item has rather than whatever the last slot left behind.
        Design.Gems = Report.Sockets.Select(s => s.Gem?.Code ?? "").ToList();
    }
}

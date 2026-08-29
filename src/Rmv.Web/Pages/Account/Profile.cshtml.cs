using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

namespace Rmv.Web.Pages.Account;

[Authorize]
public class ProfileModel(
    IServiceProvider services,
    IConfiguration config,
    IAuthorizationService authorization) : PageModel
{
    public string DiscordName => DiscordUser.Name(User);

    public string? DiscordId => DiscordUser.Id(User);

    public string? AvatarUrl => DiscordUser.AvatarUrl(User, 128);

    public Member? Record { get; private set; }

    /// <summary>
    /// The member's own characters, so the panel shows what they have rather than
    /// only offering to add more. A panel that says "add yours" to someone who
    /// already added one reads as broken.
    /// </summary>
    public IReadOnlyList<Character> Characters { get; private set; } = [];

    public bool IsRoot { get; private set; }

    public string? Notice { get; private set; }

    /// <summary>
    /// Whether this page offers to add a character, from the policy that actually
    /// gates adding one.
    ///
    /// Asked, not worked out. This used to be "IsRoot || Record.CanContribute",
    /// which was a third implementation of the question: configuration folded
    /// together with the row, by hand, in a page. That hand-folding is exactly what
    /// went wrong elsewhere, and the "IsRoot ||" was the tell. The policy already
    /// reads configuration first and the row second, so asking it gives one answer
    /// with one implementation behind it.
    /// </summary>
    public bool CanContribute { get; private set; }

    /// <summary>What the site calls them: the alias if set, otherwise Discord.</summary>
    public string Handle => Record?.Handle ?? DiscordName;

    [BindProperty]
    [StringLength(32, MinimumLength = 2, ErrorMessage = "Between 2 and 32 characters.")]
    [Display(Name = "Alias")]
    public string? Alias { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        Alias = Record?.Alias;

        if (this.Flash("saved") is not null)
        {
            Notice = "Saved.";
        }
    }

    public async Task<IActionResult> OnPostAliasAsync(CancellationToken ct)
    {
        await LoadAsync(ct);

        if (!ModelState.IsValid || Record is null)
        {
            return Page();
        }

        var db = services.GetRequiredService<RmvDbContext>();
        var row = await db.Members.FindAsync([Record.Id], ct);
        if (row is null)
        {
            return Page();
        }

        // Blank clears it, falling back to the Discord name.
        row.Alias = string.IsNullOrWhiteSpace(Alias) ? null : Alias.Trim();
        await db.SaveChangesAsync(ct);

        return RedirectToPage(new { saved = true });
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        // Still read from configuration, but only to label them as root on the page.
        // It no longer decides anything.
        IsRoot = AdminPolicy.IsRootAdmin(config, DiscordId);

        CanContribute = (await authorization.AuthorizeAsync(User, MemberPolicy.Approved)).Succeeded;

        var db = services.GetService<RmvDbContext>();
        if (db is null)
        {
            return;
        }

        try
        {
            // Through CurrentMember, the one place the signed-in member is
            // resolved. It ensures rather than looks up, so the profile is never
            // the page that tells you your account does not exist while you are
            // signed in.
            Record = await services.GetRequiredService<CurrentMember>().GetAsync(User, ct);

            if (Record is not null)
            {
                Characters = await db.Characters
                    .Include(c => c.Game)
                    .Where(c => c.MemberId == Record.Id)
                    .OrderByDescending(c => c.Game!.IsActive)
                    .ThenBy(c => c.Game!.Game)
                    .ThenBy(c => c.Name)
                    .AsNoTracking()
                    .ToListAsync(ct);
            }
        }
        catch
        {
            // The page still renders from claims alone; status shows unknown.
        }
    }
}

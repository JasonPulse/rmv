using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

namespace Rmv.Web.Pages.Account;

[Authorize]
public class ProfileModel(IServiceProvider services, CurrentMember me) : PageModel
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
    /// Whether this page offers to add a character.
    ///
    /// Read off the one access answer, not worked out here. This used to be
    /// "IsRoot || Record.CanContribute": configuration folded together with the row,
    /// by hand, in a page. That is the shape that broke the site, and the "IsRoot ||"
    /// was the tell.
    /// </summary>
    public bool CanContribute { get; private set; }

    /// <summary>
    /// Blocked, from the same answer. A root admin cannot be blocked, so this is
    /// not the row's status read on its own.
    /// </summary>
    public bool Blocked { get; private set; }

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
        // One question, one call, three facts. The row, the root label and whether
        // this page offers to add a character all come from the same answer, so the
        // panel cannot contradict the policy that gates the button it draws.
        var access = await me.AccessAsync(User, ct);

        Record = access.Member;
        IsRoot = access.IsRoot;
        CanContribute = access.CanContribute;
        Blocked = access.Blocked;

        var db = services.GetService<RmvDbContext>();
        if (db is null)
        {
            return;
        }

        try
        {
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

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

namespace Rmv.Web.Pages.Roster;

/// <summary>
/// A member and everything they have played, reached by clicking a character on
/// the history page.
///
/// Public, like the history page it is linked from: the point of a roster is that
/// people can look each other up. It shows a member's handle, never their Discord
/// id, so it does not publish an account identifier.
/// </summary>
public class MemberModel(IServiceProvider services) : PageModel
{
    public Data.Member? Owner { get; private set; }

    /// <summary>Characters grouped by game, active games first.</summary>
    public IReadOnlyList<GameGroup> Games { get; private set; } = [];

    /// <summary>The character that was clicked, highlighted on the page.</summary>
    public int? HighlightId { get; private set; }

    public Character? Highlighted { get; private set; }

    public record GameGroup(GamePresence Game, IReadOnlyList<Character> Characters);

    public async Task<IActionResult> OnGetAsync(int id, int? c, CancellationToken ct)
    {
        var db = services.GetService<RmvDbContext>();
        if (db is null)
        {
            return NotFound();
        }

        Owner = await db.Members.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);

        // A blocked member is not shown at all: they are not on the roster.
        if (Owner is null || Owner.Status == MemberStatus.Blocked)
        {
            return NotFound();
        }

        var characters = await db.Characters
            .Include(x => x.Game)
            .Where(x => x.MemberId == id)
            .AsNoTracking()
            .ToListAsync(ct);

        Games = characters
            .Where(x => x.Game is not null)
            .GroupBy(x => x.Game!)
            .OrderByDescending(g => g.Key.IsActive)
            .ThenBy(g => g.Key.SortOrder)
            .ThenBy(g => g.Key.Game)
            .Select(g => new GameGroup(
                g.Key,
                g.OrderBy(x => x.Name).ToList()))
            .ToList();

        HighlightId = c;
        Highlighted = characters.FirstOrDefault(x => x.Id == c);

        return Page();
    }
}

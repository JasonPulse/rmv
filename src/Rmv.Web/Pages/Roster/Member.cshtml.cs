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

    /// <summary>
    /// Loaded through TryLoadAsync, like every other public page.
    ///
    /// This used to resolve the DbContext and query it directly, which is the same
    /// three lines with the try left off. A page linked from the history page then
    /// answered 500 during a Postgres restart while the page linking to it rendered
    /// fine. The rule is "no public page fails because of the database", and the
    /// way to keep a rule is to call the one thing that implements it.
    /// </summary>
    public async Task<IActionResult> OnGetAsync(int id, int? c, CancellationToken ct)
    {
        HighlightId = c;

        var loaded = await this.TryLoadAsync(services, async db =>
        {
            Owner = await db.Members.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);

            if (!RosterVisibility.Shows(Owner))
            {
                Owner = null;
                return;
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

            Highlighted = characters.FirstOrDefault(x => x.Id == c);
        });

        // No database, an outage, or nobody by that id: all three are "no such
        // roster page" to a visitor, and none of them is a 500.
        return loaded && Owner is not null ? Page() : NotFound();
    }
}

using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

namespace Rmv.Web.Pages;

public class HistoryModel(IServiceProvider services) : PageModel
{
    /// <summary>
    /// One list, newest first. Active games lead, then by the year each presence
    /// ended; see GamePresence.NewestFirst.
    /// </summary>
    public IReadOnlyList<GamePresence> Games { get; private set; } = [];

    public bool DatabaseUnavailable { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) =>
        DatabaseUnavailable = !await this.TryLoadAsync(services, async db =>
        {
            var all = await db.GamePresences
                .Include(g => g.Links.OrderBy(l => l.SortOrder).ThenBy(l => l.Label))
                // Owners come along so a card can link a character to its member
                // without a query per character.
                .Include(g => g.Characters).ThenInclude(c => c.Member)
                .AsNoTracking()
                .ToListAsync(ct);

            // A blocked member is off the roster, so their characters go with them.
            foreach (var game in all)
            {
                game.Characters = game.Characters
                    .Where(c => c.Member is not null && c.Member.Status != MemberStatus.Blocked)
                    .OrderBy(c => c.Name)
                    .ToList();
            }

            // Ordered here rather than in SQL: the key comes from parsing Period,
            // which Postgres cannot do for us and which nine rows do not need it to.
            Games = all.OrderBy(g => g.NewestFirst).ToList();
        });
}

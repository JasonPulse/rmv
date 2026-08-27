using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

namespace Rmv.Web.Pages;

public class HistoryModel(IServiceProvider services) : PageModel
{
    public IReadOnlyList<GamePresence> Active { get; private set; } = [];

    public IReadOnlyList<GamePresence> Past { get; private set; } = [];

    public bool DatabaseUnavailable { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        // Resolved lazily rather than injected, so this page still renders when
        // no database is configured. Every public page has to survive that.
        var db = services.GetService<RmvDbContext>();
        if (db is null)
        {
            DatabaseUnavailable = true;
            return;
        }

        try
        {
            var all = await db.GamePresences
                .Include(g => g.Links.OrderBy(l => l.SortOrder).ThenBy(l => l.Label))
                // Owners come along so a card can link a character to its member
                // without a query per character.
                .Include(g => g.Characters).ThenInclude(c => c.Member)
                .OrderBy(g => g.SortOrder).ThenBy(g => g.Game)
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

            Active = all.Where(g => g.IsActive).ToList();
            Past = all.Where(g => !g.IsActive).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DatabaseUnavailable = true;
        }
    }
}

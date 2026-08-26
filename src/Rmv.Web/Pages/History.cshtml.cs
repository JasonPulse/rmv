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
                .OrderBy(g => g.SortOrder).ThenBy(g => g.Game)
                .AsNoTracking()
                .ToListAsync(ct);

            Active = all.Where(g => g.IsActive).ToList();
            Past = all.Where(g => !g.IsActive).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DatabaseUnavailable = true;
        }
    }
}

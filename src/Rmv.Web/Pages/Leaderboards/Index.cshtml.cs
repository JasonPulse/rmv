using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;
using Rmv.Web.Herald;

namespace Rmv.Web.Pages.Leaderboards;

/// <summary>
/// Who is ahead, per game, from the herald data the daily pass already collects.
///
/// Only games with a herald appear. A hand-typed sheet is whatever its owner
/// remembered, so ranking one against a fetched number would be comparing a fact
/// to a recollection and putting a position next to it.
///
/// The measure per game comes from its adapter, not from a column here. See
/// LeaderboardMetric.
/// </summary>
public class IndexModel(IServiceProvider services, HeraldRegistry heralds) : PageModel
{
    /// <summary>One game's table, already ordered.</summary>
    /// <param name="Metric">The heading for the value column.</param>
    public record Board(GamePresence Game, LeaderboardMetric Metric, IReadOnlyList<LeaderboardRow> Rows);

    public IReadOnlyList<Board> Boards { get; private set; } = [];

    public bool DatabaseUnavailable { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) =>
        DatabaseUnavailable = !await this.TryLoadAsync(services, async db =>
        {
            var characters = await db.Characters
                .Include(c => c.Game)
                .Include(c => c.Member)
                .FromHerald()
                .OnRoster()
                .AsNoTracking()
                .ToListAsync(ct);

            var boards = new List<Board>();

            // Grouped by id, not by the Game navigation. AsNoTracking does no
            // identity resolution, so every character carries its own GamePresence
            // instance and grouping on the object compared them by reference: one
            // board per character, each titled the same game.
            foreach (var group in characters.GroupBy(c => c.GamePresenceId))
            {
                var game = group.First().Game!;

                // The adapter has to still be registered. A game pointing at one
                // that no longer exists has no metric, so it has no board.
                if (heralds.Find(game.HeraldAdapterKey) is not { } adapter)
                {
                    continue;
                }

                var rows = Leaderboard.Rank(group, adapter.Metric.By);

                if (rows.Count > 0)
                {
                    boards.Add(new Board(game, adapter.Metric, rows));
                }
            }

            Boards = boards
                .OrderByDescending(b => b.Game.IsActive)
                .ThenBy(b => b.Game.NewestFirst)
                .ToList();
        });
}

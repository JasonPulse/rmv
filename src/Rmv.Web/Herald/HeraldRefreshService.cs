using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

namespace Rmv.Web.Herald;

/// <summary>What one pass did. Counts rather than a bool, so a log line can say.</summary>
/// <param name="Refreshed">Characters the herald answered for.</param>
/// <param name="Failed">Characters it did not. Their previous data is untouched.</param>
/// <param name="Signatures">Signatures redrawn because their characters moved.</param>
public sealed record RefreshSummary(int Refreshed, int Failed, int Signatures = 0)
{
    public static readonly RefreshSummary None = new(0, 0);

    public int Total => Refreshed + Failed;
}

/// <summary>
/// Keeps character stats and portraits current without anyone clicking anything.
///
/// This should have existed from the start. The design always said "on add, then
/// refresh daily", and only the first half was built, so a character's stats were
/// frozen at the moment it was added. That was survivable. Portraits were not: the
/// migration that introduced them deliberately cleared the old image URLs, which
/// left every existing character with no picture and the only route back being to
/// open /characters and press refresh once per character. Asking someone to do that
/// is not a feature.
///
/// Two passes, same code:
///
///   Backfill, shortly after startup, over characters with a herald and no stored
///   portrait. This is what makes a picture appear on its own.
///
///   Daily, over every herald character, for stats as well as pictures.
///
/// Politeness is the main constraint. These are other people's servers, and one of
/// them has already been hammered by a test suite of mine. Characters are refreshed
/// one at a time with a pause between, and an unchanged portrait downloads nothing
/// because the version check short-circuits it.
///
/// Nothing here throws. A herald being down leaves the previous data in place and
/// the next pass tries again. Two replicas would both run this, which is harmless:
/// a refresh writes the same values and re-downloads no images.
/// </summary>
public sealed class HeraldRefreshService(
    IServiceScopeFactory scopes,
    DatabaseState state,
    ILogger<HeraldRefreshService> log) : BackgroundService
{
    /// <summary>Long enough for the migration to have run, short enough to be soon.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan Period = TimeSpan.FromHours(24);

    /// <summary>
    /// Between characters. Small, because the lists are dozens of rows, not
    /// thousands, and a herald should never see this as a burst.
    ///
    /// Settable only so a test does not sit through it. Never shortened in
    /// production: the pause is the politeness.
    /// </summary>
    public TimeSpan BetweenCharacters { get; init; } = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);

            // The backfill runs first and only over what is missing, so a restart
            // is cheap even with a full roster.
            await RunAsync(missingPortraitsOnly: true, ct);

            using var timer = new PeriodicTimer(Period);
            do
            {
                await RunAsync(missingPortraitsOnly: false, ct);
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// One pass. Public so a test can drive it: the regression this service exists
    /// to fix was silent, and the only thing that stops it recurring is a test that
    /// asserts a character with a herald and no portrait ends the pass with one.
    /// </summary>
    /// <param name="missingPortraitsOnly">
    /// The backfill. False refreshes every herald character, stats included.
    /// </param>
    public async Task<RefreshSummary> RunAsync(bool missingPortraitsOnly, CancellationToken ct)
    {
        if (state.Status != DatabaseStatus.Ready)
        {
            log.LogInformation("Skipping herald refresh: database is {Status}.", state.Status);
            return RefreshSummary.None;
        }

        List<int> ids;

        try
        {
            // Ids first, then one scope per character. A single long-lived scope
            // would hold one DbContext and one transaction open across every herald
            // request, which is minutes of connection for no reason.
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RmvDbContext>();

            var query = db.Characters.FromHerald();

            if (missingPortraitsOnly)
            {
                query = query.Where(c => c.PortraitVersion == null);
            }

            ids = await query.OrderBy(c => c.Id).Select(c => c.Id).ToListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not list characters to refresh.");
            return RefreshSummary.None;
        }

        if (ids.Count == 0)
        {
            return RefreshSummary.None;
        }

        log.LogInformation(
            "Herald refresh starting for {Count} character(s), {Kind}.",
            ids.Count, missingPortraitsOnly ? "portrait backfill" : "daily pass");

        var refreshed = 0;
        var failed = 0;
        var owners = new HashSet<int>();

        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested)
            {
                return new RefreshSummary(refreshed, failed);
            }

            if (await RefreshOneAsync(id, owners, ct))
            {
                refreshed++;
            }
            else
            {
                failed++;
            }

            await Task.Delay(BetweenCharacters, ct);
        }

        var signatures = await RedrawSignaturesAsync(owners, ct);

        log.LogInformation(
            "Herald refresh finished: {Refreshed} refreshed, {Failed} failed, {Signatures} signature(s) redrawn.",
            refreshed, failed, signatures);

        return new RefreshSummary(refreshed, failed, signatures);
    }

    /// <summary>
    /// Redraws the signatures of everyone whose characters were just refreshed.
    ///
    /// Once per member, not once per character, and each one checks a digest before
    /// it draws anything: a member whose stats did not move costs a query and no
    /// render. This is what keeps a signature embedded in a forum post current
    /// without anybody opening the site, which is the whole point of a signature.
    ///
    /// A failure here is logged and skipped. A signature is decoration, and it must
    /// not be able to fail the pass that keeps the characters themselves current.
    /// </summary>
    private async Task<int> RedrawSignaturesAsync(HashSet<int> owners, CancellationToken ct)
    {
        var redrawn = 0;

        foreach (var memberId in owners)
        {
            if (ct.IsCancellationRequested)
            {
                return redrawn;
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var signatures = scope.ServiceProvider
                    .GetRequiredService<Rmv.Web.Signature.SignatureService>();

                if (await signatures.RefreshAsync(memberId, ct))
                {
                    redrawn++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not redraw the signature for member {Member}.", memberId);
            }
        }

        return redrawn;
    }

    private async Task<bool> RefreshOneAsync(int id, HashSet<int> owners, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RmvDbContext>();
            var characters = scope.ServiceProvider.GetRequiredService<CharacterService>();

            var character = await db.Characters
                .Include(c => c.Game)
                .FirstOrDefaultAsync(c => c.Id == id, ct);

            if (character is null)
            {
                // Removed between listing and now. Not a failure.
                return true;
            }

            // Whose signature might now be out of date. Collected rather than acted
            // on here: a member with six characters would otherwise be re-checked
            // six times in one pass.
            owners.Add(character.MemberId);

            var ok = await characters.RefreshAsync(character, ct);

            // Saved either way: a failure records LastError, which is what the
            // owner's page shows as "last refresh failed".
            await db.SaveChangesAsync(ct);

            return ok;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One bad character must not stop the pass.
            log.LogWarning(ex, "Refresh failed for character {Id}.", id);
            return false;
        }
    }
}

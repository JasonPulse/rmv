using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

namespace Rmv.Web.Herald;

public sealed record AddOutcome(bool Ok, Character? Character, string? Error)
{
    public static AddOutcome Fail(string error) => new(false, null, error);
    public static AddOutcome Added(Character c) => new(true, c, null);
}

public sealed class CharacterService(
    RmvDbContext db,
    HeraldRegistry registry,
    ILogger<CharacterService> log)
{
    /// <summary>
    /// Looks a character up on the game's herald and records it against the
    /// member. Nothing is saved unless the herald confirms the character exists,
    /// so a typo does not leave a junk row behind.
    /// </summary>
    public async Task<AddOutcome> AddAsync(
        Member member, int gameId, string rawName, CancellationToken ct)
    {
        var name = (rawName ?? "").Trim();

        var game = await db.GamePresences.FirstOrDefaultAsync(g => g.Id == gameId, ct);
        if (game is null)
        {
            return AddOutcome.Fail("That game does not exist.");
        }

        var adapter = registry.Find(game.HeraldAdapterKey);
        if (adapter is null || string.IsNullOrWhiteSpace(game.HeraldBaseUrl))
        {
            return AddOutcome.Fail($"{game.Game} has no herald configured yet. An admin has to set one.");
        }

        // Checked before the fetch so an obviously wrong name costs the herald
        // nothing.
        if (name.Length is 0 or > 32)
        {
            return AddOutcome.Fail("That does not look like a character name.");
        }

        // Case-insensitive, because heralds are and because two members claiming
        // "Arwen" and "arwen" is the same character.
        var existing = await db.Characters
            .Include(c => c.Member)
            .FirstOrDefaultAsync(c => c.GamePresenceId == gameId
                                      && c.Name.ToLower() == name.ToLower(), ct);

        if (existing is not null)
        {
            return existing.MemberId == member.Id
                ? AddOutcome.Fail($"You have already added {existing.Name}.")
                : AddOutcome.Fail(
                    $"{existing.Name} is already claimed by {existing.Member?.DisplayName ?? "another member"}. "
                    + "If that is wrong, ask an admin.");
        }

        var result = await adapter.FetchCharacterAsync(game.HeraldBaseUrl!, name, ct);
        if (!result.Ok || result.Character is null)
        {
            return AddOutcome.Fail(result.Error ?? "The herald did not recognise that name.");
        }

        var now = DateTimeOffset.UtcNow;
        var character = new Character
        {
            MemberId = member.Id,
            GamePresenceId = gameId,
            AddedAt = now,
            LastFetchedAt = now,
        };

        Apply(character, result.Character);
        db.Characters.Add(character);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // The unique index caught a race between two submits. Reporting it as
            // "already claimed" is truthful and better than a 500.
            log.LogInformation(ex, "Duplicate character add for {Name} on game {Game}.", name, gameId);
            return AddOutcome.Fail($"{name} was just claimed by someone else.");
        }

        return AddOutcome.Added(character);
    }

    /// <summary>Refreshes one character in place. Failures are recorded, not thrown.</summary>
    public async Task<bool> RefreshAsync(Character character, CancellationToken ct)
    {
        var game = character.Game
                   ?? await db.GamePresences.FirstOrDefaultAsync(g => g.Id == character.GamePresenceId, ct);

        var adapter = registry.Find(game?.HeraldAdapterKey);
        if (adapter is null || game is null || string.IsNullOrWhiteSpace(game.HeraldBaseUrl))
        {
            character.LastError = "No herald configured for this game.";
            return false;
        }

        var result = await adapter.FetchCharacterAsync(game.HeraldBaseUrl!, character.Name, ct);
        character.LastFetchedAt = DateTimeOffset.UtcNow;

        if (!result.Ok || result.Character is null)
        {
            // The previous stats are kept: a herald being down should not blank a
            // character that was fine yesterday.
            character.LastError = result.Error;
            return false;
        }

        Apply(character, result.Character);
        return true;
    }

    private static void Apply(Character target, HeraldCharacter source)
    {
        // The name is taken from the herald's echo of it, so capitalisation
        // matches the game rather than whatever was typed.
        target.Name = source.Name;
        target.Guild = source.Guild;
        target.Realm = source.Realm;
        target.Class = source.Class;
        target.Race = source.Race;
        target.Level = source.Level;
        target.RealmRank = source.RealmRank;
        target.Score = source.RealmPoints;
        target.Kills = source.Kills;
        target.Deaths = source.Deaths;
        target.LastOnline = source.LastOnline;
        target.HeraldUrl = source.Url;
        target.LastError = null;
    }
}

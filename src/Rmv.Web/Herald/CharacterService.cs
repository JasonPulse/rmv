using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

namespace Rmv.Web.Herald;

public sealed record AddOutcome(bool Ok, Character? Character, string? Error)
{
    public static AddOutcome Fail(string error) => new(false, null, error);
    public static AddOutcome Added(Character c) => new(true, c, null);
}

/// <summary>
/// What a member asked for when adding a character.
/// </summary>
/// <param name="UseHerald">
/// What the member chose, and only consulted for a herald that admits it does not
/// list everyone; see IHeraldAdapter.CoverageNote. For every other game the game
/// decides, because a member choosing "type it in" against a working herald would
/// only be choosing worse data.
/// </param>
public sealed record CharacterRequest(
    int GameId, string Name, string? Class = null, int? Level = null, bool UseHerald = true);

public sealed class CharacterService(
    RmvDbContext db,
    HeraldRegistry registry,
    HeraldFetcher fetcher,
    ILogger<CharacterService> log)
{
    /// <summary>
    /// Adds a character, looked up or typed in, and decides which of those it is.
    ///
    /// The decision lives here and nowhere else. It used to be an expression on the
    /// characters page, which was fine while that page was the only caller and
    /// exactly the shape that goes wrong when it stops being.
    ///
    /// Three inputs: whether the game has an adapter registered, whether that
    /// adapter admits it does not list every character, and what the member chose.
    /// A game with no herald is always typed in. A game whose herald lists
    /// everybody is always looked up. Only in between does the member's choice
    /// count.
    /// </summary>
    public async Task<AddOutcome> AddAsync(
        Member member, CharacterRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var game = await db.GamePresences.FirstOrDefaultAsync(g => g.Id == request.GameId, ct);
        if (game is null)
        {
            return AddOutcome.Fail("That game does not exist.");
        }

        var adapter = registry.Find(game.HeraldAdapterKey);

        if (adapter is null)
        {
            return await AddManualAsync(
                member, request.GameId, request.Name, request.Class, request.Level, ct);
        }

        if (request.UseHerald)
        {
            return await AddAsync(member, request.GameId, request.Name, ct);
        }

        if (adapter.CoverageNote is null)
        {
            return AddOutcome.Fail(
                $"{game.Game} has a herald, so its characters are looked up rather than typed.");
        }

        return await AddManualAsync(
            member, request.GameId, request.Name, request.Class, request.Level, ct);
    }

    /// <summary>
    /// Looks a character up on the game's herald and records it against the
    /// member. Nothing is saved unless the herald confirms the character exists,
    /// so a typo does not leave a junk row behind.
    /// </summary>
    public async Task<AddOutcome> AddAsync(
        Member member, int gameId, string rawName, CancellationToken ct)
    {
        var typed = (rawName ?? "").Trim();

        var game = await db.GamePresences.FirstOrDefaultAsync(g => g.Id == gameId, ct);
        if (game is null)
        {
            return AddOutcome.Fail("That game does not exist.");
        }

        var adapter = registry.Find(game.HeraldAdapterKey);
        if (adapter is null)
        {
            return AddOutcome.Fail($"{game.Game} has no herald, so its characters are typed in by hand.");
        }

        // A loose bound only, because what a member types is not always a name:
        // see CharacterLimits.MaxTyped.
        if (typed.Length is < CharacterLimits.MinName or > CharacterLimits.MaxTyped)
        {
            return AddOutcome.Fail("That does not look like a character name.");
        }

        var result = await adapter.FetchCharacterAsync(HeraldAddress.For(game, adapter), typed, ct);
        if (!result.Ok || result.Character is null)
        {
            return AddOutcome.Fail(result.Error ?? "The herald did not recognise that name.");
        }

        // Checked against the herald's own spelling rather than the input. Two
        // members can reach one character by different routes, one typing the name
        // and one pasting a URL, and only the resolved name catches that.
        var resolved = result.Character.Name.Trim();
        if (await ClaimedByAsync(gameId, resolved, ct) is { } existing)
        {
            return AddOutcome.Fail(Claimed(existing, member.Id));
        }

        var character = NewCharacter(member, gameId, CharacterSource.Herald);
        Apply(character, result.Character);
        await SyncPortraitAsync(character, result.Character.Portrait, ct);

        return await SaveNewAsync(character, resolved, gameId, ct);
    }

    /// <summary>
    /// Records a character the member typed out themselves, for a game with no
    /// herald to ask.
    ///
    /// This is the ordinary case, not a fallback: most of the games the guild has
    /// been through are on servers that never had a herald, or no longer have one
    /// running. The row is the source of truth here, so the owner can edit it and
    /// nothing ever overwrites it.
    /// </summary>
    public async Task<AddOutcome> AddManualAsync(
        Member member, int gameId, string rawName, string? jobClass, int? level, CancellationToken ct)
    {
        var name = (rawName ?? "").Trim();

        var game = await db.GamePresences.FirstOrDefaultAsync(g => g.Id == gameId, ct);
        if (game is null)
        {
            return AddOutcome.Fail("That game does not exist.");
        }

        if (!IsPlausibleName(name))
        {
            return AddOutcome.Fail("That does not look like a character name.");
        }

        if (await ClaimedByAsync(gameId, name, ct) is { } existing)
        {
            return AddOutcome.Fail(Claimed(existing, member.Id));
        }

        // Whether a herald game may be typed in at all is decided by the AddAsync
        // above, which is the only place that knows what the member was offered.
        // Nothing here overwrites a typed sheet: a manual character has
        // Source = Manual, RefreshAsync leaves those alone, and the daily pass
        // filters to FromHerald.

        if (!TryTidy(jobClass, level, out var tidyClass, out var tidyLevel, out var error))
        {
            return AddOutcome.Fail(error!);
        }

        var character = NewCharacter(member, gameId, CharacterSource.Manual);
        character.Name = name;
        character.Class = tidyClass;
        character.Level = tidyLevel;
        // Nothing fetched it, so there is no fetch time to report and no error.
        character.LastFetchedAt = null;

        return await SaveNewAsync(character, name, gameId, ct);
    }

    /// <summary>
    /// Edits a manual character in place. Herald characters are refused: their
    /// stats come from the herald, and letting them be typed over would make the
    /// next refresh look like it lost someone's edit.
    /// </summary>
    public async Task<AddOutcome> UpdateManualAsync(
        Character character, string rawName, string? jobClass, int? level, CancellationToken ct)
    {
        if (!character.IsManual)
        {
            return AddOutcome.Fail($"{character.Name} comes from a herald. Refresh it instead of editing it.");
        }

        var name = (rawName ?? "").Trim();
        if (!IsPlausibleName(name))
        {
            return AddOutcome.Fail("That does not look like a character name.");
        }

        if (!TryTidy(jobClass, level, out var tidyClass, out var tidyLevel, out var error))
        {
            return AddOutcome.Fail(error!);
        }

        // Only when the name actually changed, so saving an unchanged sheet does
        // not report the character as claimed by its own owner.
        if (!string.Equals(name, character.Name, StringComparison.OrdinalIgnoreCase)
            && await ClaimedByAsync(character.GamePresenceId, name, ct) is { } taken)
        {
            return AddOutcome.Fail(Claimed(taken, character.MemberId));
        }

        character.Name = name;
        character.Class = tidyClass;
        character.Level = tidyLevel;

        await db.SaveChangesAsync(ct);
        return AddOutcome.Added(character);
    }

    // --- shared plumbing -----------------------------------------------------

    /// <summary>
    /// Case-insensitive, because heralds are and because two members claiming
    /// "Arwen" and "arwen" is the same character.
    /// </summary>
    private Task<Character?> ClaimedByAsync(int gameId, string name, CancellationToken ct) =>
        db.Characters
            .Include(c => c.Member)
            .FirstOrDefaultAsync(c => c.GamePresenceId == gameId
                                      && c.Name.ToLower() == name.ToLower(), ct);

    private static string Claimed(Character existing, int memberId) =>
        existing.MemberId == memberId
            ? $"You have already added {existing.Name}."
            : $"{existing.Name} is already claimed by {existing.Member?.Handle ?? "another member"}. "
              + "If that is wrong, ask an admin.";

    private static bool IsPlausibleName(string name) =>
        name.Length is >= CharacterLimits.MinName and <= CharacterLimits.MaxName;

    private static Character NewCharacter(Member member, int gameId, CharacterSource source)
    {
        var now = DateTimeOffset.UtcNow;
        return new Character
        {
            MemberId = member.Id,
            GamePresenceId = gameId,
            Source = source,
            AddedAt = now,
            LastFetchedAt = now,
        };
    }

    /// <summary>
    /// Job and level as the member typed them. Blank is allowed for both: a level
    /// nobody remembers is better recorded as absent than as a guess.
    /// </summary>
    private static bool TryTidy(
        string? jobClass, int? level, out string? tidyClass, out int? tidyLevel, out string? error)
    {
        tidyClass = string.IsNullOrWhiteSpace(jobClass) ? null : jobClass.Trim();
        tidyLevel = level;
        error = null;

        if (tidyClass is { Length: > CharacterLimits.MaxClass })
        {
            error = "That job or class name is too long.";
            return false;
        }

        if (level is < CharacterLimits.MinLevel or > CharacterLimits.MaxLevel)
        {
            error = $"Level has to be between {CharacterLimits.MinLevel} "
                    + $"and {CharacterLimits.MaxLevel}.";
            return false;
        }

        return true;
    }

    private async Task<AddOutcome> SaveNewAsync(
        Character character, string name, int gameId, CancellationToken ct)
    {
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

        if (character.IsManual)
        {
            // Not an error, so LastError stays clear: a hand-typed sheet is not
            // stale just because nothing fetched it.
            return false;
        }

        var adapter = registry.Find(game?.HeraldAdapterKey);
        if (adapter is null || game is null)
        {
            character.LastError = "No herald configured for this game.";
            return false;
        }

        var result = await adapter.FetchCharacterAsync(HeraldAddress.For(game, adapter), character.Name, ct);
        character.LastFetchedAt = DateTimeOffset.UtcNow;

        if (!result.Ok || result.Character is null)
        {
            // The previous stats are kept: a herald being down should not blank a
            // character that was fine yesterday.
            character.LastError = result.Error;
            return false;
        }

        Apply(character, result.Character);
        await SyncPortraitAsync(character, result.Character.Portrait, ct);
        return true;
    }

    /// <summary>
    /// Brings the stored portrait into line with what the herald offers.
    ///
    /// Downloads only when the version changed, which is what makes a daily refresh
    /// across every character cheap and polite. The FFXI herald asks for exactly
    /// this: "poll appearances and re-render only where hash changed".
    ///
    /// A failure leaves the previous picture in place and is not recorded as a
    /// character error. A portrait is decoration; losing it should not make a
    /// character look stale, and a herald that has dropped its renderer should not
    /// blank everyone's picture.
    /// </summary>
    private async Task SyncPortraitAsync(
        Character character, HeraldPortrait? portrait, CancellationToken ct)
    {
        if (portrait is null)
        {
            return;
        }

        // Unchanged version and bytes actually present. The second half matters:
        // a refresh interrupted between writing the bytes and writing the version
        // would otherwise be skipped forever, leaving a character claiming a
        // picture the endpoint cannot serve. One indexed existence check per
        // character per refresh buys self-healing.
        if (character.PortraitVersion == portrait.Tag
            && await db.CharacterPortraits.AnyAsync(p => p.CharacterId == character.Id, ct))
        {
            return;
        }

        var fetched = await fetcher.GetImageAsync(portrait.Url, ct);
        if (!fetched.Ok || fetched.Bytes is null)
        {
            log.LogInformation(
                "No portrait for {Name}: {Error}", character.Name, fetched.Error);
            return;
        }

        var row = character.Portrait
                  ?? await db.CharacterPortraits.FirstOrDefaultAsync(p => p.CharacterId == character.Id, ct);

        if (row is null)
        {
            row = new CharacterPortrait { Character = character };
            db.CharacterPortraits.Add(row);
            character.Portrait = row;
        }

        row.Bytes = fetched.Bytes;
        row.ContentType = fetched.ContentType!;
        row.Version = portrait.Tag;
        row.FetchedAt = DateTimeOffset.UtcNow;

        // Only after the bytes are in hand, so a failed download cannot leave a
        // version claiming a picture we do not have.
        character.PortraitVersion = portrait.Tag;
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

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
/// <summary>
/// What a member types for a game with no herald, and the fields a signature's
/// %Class%, %Race% and %Level% draw on for such a character.
///
/// One record rather than three parameters repeated down four signatures. Adding
/// race was the third field, which is where a parameter list stops reading.
/// </summary>
public sealed record CharacterSheet(string? Class = null, string? Race = null, int? Level = null)
{
    public static readonly CharacterSheet Blank = new();
}

public sealed record CharacterRequest(
    int GameId, string Name, CharacterSheet? Sheet = null, bool UseHerald = true);

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
                member, request.GameId, request.Name, request.Sheet, ct);
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
            member, request.GameId, request.Name, request.Sheet, ct);
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
        Member member, int gameId, string rawName, CharacterSheet? sheet, CancellationToken ct)
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

        if (!TryTidy(sheet, out var tidy, out var error))
        {
            return AddOutcome.Fail(error!);
        }

        var character = NewCharacter(member, gameId, CharacterSource.Manual);
        character.Name = name;
        character.Class = tidy.Class;
        character.Race = tidy.Race;
        character.Level = tidy.Level;
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
        Character character, string rawName, CharacterSheet? sheet, CancellationToken ct)
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

        if (!TryTidy(sheet, out var tidy, out var error))
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
        character.Class = tidy.Class;
        character.Race = tidy.Race;
        character.Level = tidy.Level;

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
    /// The sheet as the member typed it. Every field may be blank: a level nobody
    /// remembers is better recorded as absent than as a guess.
    /// </summary>
    private static bool TryTidy(CharacterSheet? sheet, out CharacterSheet tidy, out string? error)
    {
        sheet ??= CharacterSheet.Blank;
        error = null;

        tidy = new CharacterSheet(
            Class: Trimmed(sheet.Class),
            Race: Trimmed(sheet.Race),
            Level: sheet.Level);

        if (tidy.Class is { Length: > CharacterLimits.MaxClass })
        {
            error = "That job or class name is too long.";
            return false;
        }

        if (tidy.Race is { Length: > CharacterLimits.MaxRace })
        {
            error = "That race is too long.";
            return false;
        }

        if (tidy.Level is < CharacterLimits.MinLevel or > CharacterLimits.MaxLevel)
        {
            error = $"Level has to be between {CharacterLimits.MinLevel} "
                    + $"and {CharacterLimits.MaxLevel}.";
            return false;
        }

        return true;
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
    /// Brings the stored portrait into line with what the herald is serving.
    ///
    /// The picture is its own version. The bytes are fetched and digested, and the
    /// digest is what gets stored and what appears in our portrait URL, so the URL
    /// changes exactly when the picture does and never otherwise.
    ///
    /// This used to ask the herald whether the picture had changed and skip the
    /// download when it said no. Every herald answers that question badly, each in
    /// its own way, and the FFXI one answers it wrongly: on 2026-08-30 it served
    /// two visibly different renders of one character under one appearance hash,
    /// while sending that hash as an ETag and marking the response immutable. A
    /// stored portrait keyed on anything the herald says about it stops updating
    /// and nothing on either side reports a problem.
    ///
    /// The cost is one image fetch per character per pass, which for this roster is
    /// under a megabyte a day, against a file the herald already has on disk. That
    /// is the whole price of never showing yesterday's armour.
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

        var fetched = await fetcher.GetImageAsync(portrait.Url, ct);
        if (!fetched.Ok || fetched.Bytes is null)
        {
            log.LogInformation(
                "No portrait for {Name}: {Error}", character.Name, fetched.Error);
            return;
        }

        var version = VersionOf(fetched.Bytes);

        var row = character.Portrait
                  ?? await db.CharacterPortraits.FirstOrDefaultAsync(p => p.CharacterId == character.Id, ct);

        // Same picture, and it is actually stored. The second half matters: a
        // refresh interrupted between writing the bytes and writing the version
        // would otherwise leave a character claiming a picture the endpoint cannot
        // serve, and nothing would ever fill it in.
        if (row is not null && row.Version == version && character.PortraitVersion == version)
        {
            return;
        }

        if (row is null)
        {
            row = new CharacterPortrait { Character = character };
            db.CharacterPortraits.Add(row);
            character.Portrait = row;
        }

        if (row.Version != version)
        {
            log.LogInformation(
                "New portrait for {Name}: {Old} to {New}.",
                character.Name, row.Version.Length == 0 ? "none" : row.Version, version);
        }

        row.Bytes = fetched.Bytes;
        row.ContentType = fetched.ContentType!;
        row.Version = version;
        row.FetchedAt = DateTimeOffset.UtcNow;

        // Only after the bytes are in hand, so a failed download cannot leave a
        // version claiming a picture we do not have.
        character.PortraitVersion = version;
    }

    /// <summary>
    /// A picture's identity: a short digest of its bytes.
    ///
    /// Sixteen hex characters, which is what the column holds and is far more than
    /// enough. A collision here would have to be between two successive pictures of
    /// one character, and would mean a stale portrait rather than the wrong
    /// person's, because the character id is separate from this.
    /// </summary>
    public static string VersionOf(byte[] bytes) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16];

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
        target.Stats = HeraldStats.Serialise(source.Stats);
        target.LastError = null;
    }
}

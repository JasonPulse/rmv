using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Rmv.Web.Data;
using Rmv.Web.Herald;

namespace Rmv.Web.Tests;

/// <summary>
/// The service's own logic: claiming, duplicates, refresh, and not leaving a row
/// behind when a name is wrong. Against a real Postgres, because the unique index
/// is part of what is being tested, but against a fake herald.
///
/// The heralds belong to other people. An earlier version of this class hit
/// Blackthorn eight times per run and started failing once I had run it a few
/// times in a minute; that looked like a bug and was my suite being rude. Real
/// heralds are exercised by saved fixtures and by the opt-in HeraldLiveTests.
///
/// Needs RMV_TEST_POSTGRES, so tagged Database and excluded from CI.
/// </summary>
public class CharacterServiceTests : HeraldDatabaseTests
{
    private CharacterService _service = null!;

    /// <summary>
    /// Only Sable has a picture, so the portrait path is opt-in and the other
    /// tests are unaffected by it.
    /// </summary>
    protected override void ConfigureHerald(FakeHeraldAdapter herald) => herald
        .WithCharacter("Enchantress")
        .WithCharacter("Balder", b => b.Realm = "Midgard")
        .WithCharacter("Fetva")
        .WithCharacter("Teagan")
        .WithCharacter("Sable", b => b.Portrait =
            new HeraldPortrait("https://fake.test/portraits/1.png?v=aaa"));

    /// <summary>A second herald game, for the cross-game claim checks.</summary>
    private int _ffxiId;

    protected override async Task SeedAsync()
    {
        _ffxiId = await NewGameAsync(withHerald: true);

        _service = new CharacterService(
            Db,
            new HeraldRegistry([Herald]),
            Fetcher,
            NullLogger<CharacterService>.Instance);
    }

    // --- which path an add takes ---------------------------------------------
    //
    // One method decides, from the game, its adapter and what the member chose, in
    // that order. The characters page used to decide it in an expression, which was
    // fine while it was the only caller.
    //
    // The Armory is why the member's choice exists at all: it lists characters on a
    // subscribed account only, and answers a lapsed one exactly as it answers a
    // misspelling, so "look it up" cannot be the only way to record a WoW character.

    private Task<AddOutcome> RequestAsync(
        int gameId, string name, bool useHerald, string? job = null, int? level = null) =>
        _service.AddAsync(
            Member, new CharacterRequest(gameId, name, new CharacterSheet(job, null, level), useHerald),
            default);

    [Fact]
    public async Task A_herald_game_is_looked_up_when_the_member_wants_it()
    {
        var outcome = await RequestAsync(HeraldGameId, "Enchantress", useHerald: true);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal(CharacterSource.Herald, outcome.Character!.Source);
        Assert.Equal("Champion", outcome.Character.Class);
    }

    [Fact]
    public async Task A_herald_that_lists_everyone_is_not_optional()
    {
        // The rule that was there before and still is: the game decides. Choosing
        // to type a sheet in against a herald that works is choosing worse data.
        Assert.Null(((IHeraldAdapter)Herald).CoverageNote);

        var outcome = await RequestAsync(HeraldGameId, "Enchantress", useHerald: false, job: "Typed");

        Assert.False(outcome.Ok);
        Assert.Contains("looked up rather than typed", outcome.Error!);
        Assert.False(await Db.Characters.AnyAsync(c => c.Name == "Enchantress"));
    }

    [Fact]
    public async Task A_herald_that_admits_it_misses_people_can_be_skipped()
    {
        // Standing in for the Armory.
        Herald.CoverageNote = "Only lists subscribed accounts.";

        var outcome = await RequestAsync(
            HeraldGameId, "Unsubscribed", useHerald: false, job: "Frost Death Knight", level: 80);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal(CharacterSource.Manual, outcome.Character!.Source);
        Assert.Equal("Frost Death Knight", outcome.Character.Class);
        Assert.Equal(80, outcome.Character.Level);
        // The herald was never asked, which is the point: it does not have them.
        Assert.Equal(0, Herald.Calls);
    }

    [Fact]
    public async Task A_typed_sheet_on_a_herald_game_is_never_overwritten()
    {
        // The reason typing one used to be refused outright. It is safe because the
        // row says where it came from: RefreshAsync leaves a manual character alone
        // and the daily pass filters to FromHerald.
        Herald.CoverageNote = "Only lists subscribed accounts.";

        var added = await RequestAsync(HeraldGameId, "Enchantress", useHerald: false, job: "Typed", level: 42);
        Assert.True(added.Ok, added.Error);

        var refreshed = await _service.RefreshAsync(added.Character!, default);

        Assert.False(refreshed);
        Assert.Equal("Typed", added.Character!.Class);
        Assert.Equal(42, added.Character.Level);
        // Not an error either: a hand-typed sheet is not stale because nothing
        // fetched it.
        Assert.Null(added.Character.LastError);
    }

    [Fact]
    public async Task A_game_with_no_herald_is_typed_in_whatever_was_ticked()
    {
        // There is nothing to look up, so the checkbox cannot ask for one.
        var outcome = await RequestAsync(NoHeraldGameId, "Sigrun", useHerald: true, job: "Skald", level: 44);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal(CharacterSource.Manual, outcome.Character!.Source);
        Assert.Equal("Skald", outcome.Character.Class);
    }

    [Fact]
    public async Task A_game_that_does_not_exist_is_refused_once()
    {
        var outcome = await RequestAsync(999_999, "Nobody", useHerald: true);

        Assert.False(outcome.Ok);
        Assert.Contains("does not exist", outcome.Error!);
    }

    [Fact]
    public async Task Adds_a_character_with_the_stats_the_herald_gave()
    {
        var outcome = await _service.AddAsync(Member, HeraldGameId, "Enchantress", default);

        Assert.True(outcome.Ok, outcome.Error);
        var c = outcome.Character!;
        Assert.Equal("Enchantress", c.Name);
        Assert.Equal(50, c.Level);
        Assert.Equal("Hibernia", c.Realm);
        Assert.NotNull(c.Score);
        Assert.NotNull(c.LastFetchedAt);
        Assert.Null(c.LastError);
    }

    [Fact]
    public async Task Stores_the_heralds_capitalisation_not_what_was_typed()
    {
        // Someone types "enchantress"; the character is called "Enchantress".
        var outcome = await _service.AddAsync(Member, HeraldGameId, "enchantress", default);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal("Enchantress", outcome.Character!.Name);
    }

    [Fact]
    public async Task A_herald_that_is_down_does_not_blank_existing_stats()
    {
        var added = await _service.AddAsync(Member, HeraldGameId, "Balder", default);
        Assert.True(added.Ok, added.Error);
        var c = added.Character!;

        Herald.ForcedError = "Herald returned 503.";
        var ok = await _service.RefreshAsync(c, default);
        await Db.SaveChangesAsync();

        Assert.False(ok);
        // The stats from the good fetch survive; only the error is new.
        Assert.Equal(50, c.Level);
        Assert.Equal("Midgard", c.Realm);
        Assert.Contains("503", c.LastError!);
    }

    [Fact]
    public async Task A_name_the_herald_does_not_know_saves_nothing()
    {
        var before = await Db.Characters.CountAsync();

        var outcome = await _service.AddAsync(Member, HeraldGameId, "Nobodyhere", default);

        Assert.False(outcome.Ok);
        Assert.Contains("no character", outcome.Error!, StringComparison.OrdinalIgnoreCase);
        // The point: a typo must not leave a junk row behind.
        Assert.Equal(before, await Db.Characters.CountAsync());
    }

    [Fact]
    public async Task The_same_character_cannot_be_added_twice()
    {
        var first = await _service.AddAsync(Member, HeraldGameId, "Balder", default);
        Assert.True(first.Ok, first.Error);

        var second = await _service.AddAsync(Member, HeraldGameId, "Balder", default);

        Assert.False(second.Ok);
        Assert.Contains("already", second.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Claiming_is_case_insensitive()
    {
        var first = await _service.AddAsync(Member, HeraldGameId, "Fetva", default);
        Assert.True(first.Ok, first.Error);

        // "fetva" is the same character as "Fetva".
        var second = await _service.AddAsync(Member, HeraldGameId, "fetva", default);

        Assert.False(second.Ok);
        Assert.Contains("already", second.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refresh_updates_the_stats_and_clears_the_error()
    {
        var added = await _service.AddAsync(Member, HeraldGameId, "Teagan", default);
        Assert.True(added.Ok, added.Error);

        var c = added.Character!;
        c.LastError = "something stale";
        c.Level = 1;
        await Db.SaveChangesAsync();

        var ok = await _service.RefreshAsync(c, default);
        await Db.SaveChangesAsync();

        Assert.True(ok);
        Assert.Equal(50, c.Level);
        Assert.Null(c.LastError);
    }

    // --- games with no herald ------------------------------------------------

    [Fact]
    public async Task Records_a_hand_typed_sheet_for_a_game_with_no_herald()
    {
        var outcome = await _service.AddManualAsync(
            Member, NoHeraldGameId, "Sigrun", new CharacterSheet("Warden", null, 44), default);

        Assert.True(outcome.Ok, outcome.Error);
        var c = outcome.Character!;
        Assert.Equal("Sigrun", c.Name);
        Assert.Equal("Warden", c.Class);
        Assert.Equal(44, c.Level);
        Assert.Equal(CharacterSource.Manual, c.Source);
        Assert.True(c.IsManual);
        // Nothing fetched it, so there is no fetch to date and no failure.
        Assert.Null(c.LastFetchedAt);
        Assert.Null(c.LastError);
        // The name is kept as typed: there is no herald to correct it.
        Assert.Equal(0, Herald.Calls);
    }

    [Fact]
    public async Task A_sheet_may_leave_the_job_and_level_blank()
    {
        // Fifteen years on, plenty of these are a name and nothing else.
        var outcome = await _service.AddManualAsync(
            Member, NoHeraldGameId, "Halvard", CharacterSheet.Blank, default);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Null(outcome.Character!.Class);
        Assert.Null(outcome.Character.Level);
    }

    [Fact]
    public async Task Refusing_a_sheet_for_a_game_that_has_a_herald()
    {
        // Still refused, one layer up. AddManualAsync records what it is given;
        // whether a herald game may be typed in at all is AddAsync's decision,
        // because that is the only place that knows what the member was offered.
        // Asserted here through the same call the page makes.
        var outcome = await RequestAsync(HeraldGameId, "Enchantress", useHerald: false, job: "Champion");

        Assert.False(outcome.Ok);
        Assert.Contains("looked up", outcome.Error);
    }

    [Fact]
    public async Task Refusing_a_herald_lookup_for_a_game_that_has_none()
    {
        var outcome = await _service.AddAsync(Member, NoHeraldGameId, "Sigrun", default);

        Assert.False(outcome.Ok);
        Assert.Contains("typed in by hand", outcome.Error);
    }

    [Fact]
    public async Task Refreshing_a_sheet_does_not_mark_it_stale()
    {
        // The reported shape of this bug: a perfectly good sheet showing a "last
        // refresh failed" warning, because nothing was ever going to refresh it.
        var added = await _service.AddManualAsync(
            Member, NoHeraldGameId, "Sigrun", new CharacterSheet("Warden", null, 44), default);
        Assert.True(added.Ok, added.Error);

        var ok = await _service.RefreshAsync(added.Character!, default);

        Assert.False(ok);
        Assert.Null(added.Character!.LastError);
        Assert.Equal("Warden", added.Character.Class);
        Assert.Equal(44, added.Character.Level);
    }

    [Fact]
    public async Task Editing_a_sheet_corrects_it_in_place()
    {
        var added = await _service.AddManualAsync(
            Member, NoHeraldGameId, "Sigrun", new CharacterSheet("Warden", null, 44), default);
        Assert.True(added.Ok, added.Error);
        var id = added.Character!.Id;

        var edited = await _service.UpdateManualAsync(
            added.Character, "Sigrunn", new CharacterSheet("Druid", null, 50), default);

        Assert.True(edited.Ok, edited.Error);
        Assert.Equal(id, edited.Character!.Id);

        var reread = await Db.Characters.AsNoTracking().FirstAsync(c => c.Id == id);
        Assert.Equal("Sigrunn", reread.Name);
        Assert.Equal("Druid", reread.Class);
        Assert.Equal(50, reread.Level);
    }

    [Fact]
    public async Task Saving_an_unchanged_sheet_is_not_a_name_clash()
    {
        // The name check has to skip the row being edited, or saving a sheet
        // reports it as already claimed by its own owner.
        var added = await _service.AddManualAsync(
            Member, NoHeraldGameId, "Sigrun", new CharacterSheet("Warden", null, 44), default);
        Assert.True(added.Ok, added.Error);

        var edited = await _service.UpdateManualAsync(
            added.Character!, "Sigrun", new CharacterSheet("Warden", null, 45), default);

        Assert.True(edited.Ok, edited.Error);
        Assert.Equal(45, edited.Character!.Level);
    }

    [Fact]
    public async Task A_sheet_cannot_be_renamed_over_someone_elses_character()
    {
        var mine = await _service.AddManualAsync(Member, NoHeraldGameId, "Sigrun", CharacterSheet.Blank, default);
        var theirs = await _service.AddManualAsync(Member, NoHeraldGameId, "Halvard", CharacterSheet.Blank, default);
        Assert.True(mine.Ok, mine.Error);
        Assert.True(theirs.Ok, theirs.Error);

        var edited = await _service.UpdateManualAsync(mine.Character!, "Halvard", CharacterSheet.Blank, default);

        Assert.False(edited.Ok);
        Assert.Contains("already added", edited.Error);
    }

    [Fact]
    public async Task A_herald_character_is_not_hand_editable()
    {
        var added = await _service.AddAsync(Member, HeraldGameId, "Fetva", default);
        Assert.True(added.Ok, added.Error);

        var edited = await _service.UpdateManualAsync(added.Character!, "Fetva", new CharacterSheet("Wizard", null, 1), default);

        Assert.False(edited.Ok);
        Assert.Contains("Refresh it", edited.Error);
        Assert.Equal("Champion", added.Character!.Class);
    }

    [Fact]
    public async Task A_typed_race_is_stored_and_can_be_corrected()
    {
        var added = await _service.AddManualAsync(
            Member, NoHeraldGameId, "Sigrun", new CharacterSheet("Warden", "Firbolg", 44), default);

        Assert.True(added.Ok, added.Error);
        Assert.Equal("Firbolg", added.Character!.Race);

        var edited = await _service.UpdateManualAsync(
            added.Character, "Sigrun", new CharacterSheet("Warden", "Celt", 44), default);

        Assert.True(edited.Ok, edited.Error);
        Assert.Equal("Celt", edited.Character!.Race);

        // And cleared, for somebody who filled it in by mistake.
        var cleared = await _service.UpdateManualAsync(
            added.Character, "Sigrun", new CharacterSheet("Warden", "  ", 44), default);

        Assert.True(cleared.Ok, cleared.Error);
        Assert.Null(cleared.Character!.Race);
    }

    [Fact]
    public async Task A_race_longer_than_the_column_is_refused_rather_than_truncated()
    {
        var outcome = await _service.AddManualAsync(
            Member, NoHeraldGameId, "Sigrun",
            new CharacterSheet(null, new string('x', CharacterLimits.MaxRace + 1), null), default);

        Assert.False(outcome.Ok);
        Assert.Contains("too long", outcome.Error);
        Assert.False(await Db.Characters.AnyAsync(c => c.GamePresenceId == NoHeraldGameId));
    }

    [Fact]
    public async Task A_level_outside_the_range_is_refused()
    {
        Assert.False((await _service.AddManualAsync(
            Member, NoHeraldGameId, "Sigrun", new CharacterSheet("Warden", null, 0), default)).Ok);
        Assert.False((await _service.AddManualAsync(
            Member, NoHeraldGameId, "Sigrun", new CharacterSheet("Warden", null, 1000), default)).Ok);

        // Nothing was written by either attempt.
        Assert.False(await Db.Characters.AnyAsync(c => c.GamePresenceId == NoHeraldGameId));
    }

    [Fact]
    public async Task An_existing_herald_character_stays_a_herald_character()
    {
        // The migration backfills Source for rows added before it existed, and
        // Herald is the truthful value: every one of them was fetched.
        var added = await _service.AddAsync(Member, HeraldGameId, "Teagan", default);

        Assert.True(added.Ok, added.Error);
        Assert.Equal(CharacterSource.Herald, added.Character!.Source);
        Assert.False(added.Character.IsManual);
    }

    // --- portraits -----------------------------------------------------------

    /// <summary>
    /// What the service stores for a given picture. Derived rather than hardcoded:
    /// a literal digest in an assertion is unreadable and says nothing about why it
    /// is that value.
    /// </summary>
    private static string Version(byte[] bytes) => CharacterService.VersionOf(bytes);

    [Fact]
    public async Task Stores_the_portrait_bytes_rather_than_a_link_to_them()
    {
        // The FFXI herald is internal: a visitor's browser cannot reach it, so a
        // link would render a broken image for everyone.
        var added = await _service.AddAsync(Member, HeraldGameId, "Sable", default);
        Assert.True(added.Ok, added.Error);

        var c = added.Character!;
        // A digest of the bytes, so the URL changes exactly when the picture does.
        Assert.Equal(Version(StubImageHandler.Png), c.PortraitVersion);
        Assert.Equal($"/characters/{c.Id}/portrait?v={c.PortraitVersion}", c.PortraitPath);
        Assert.Equal(16, c.PortraitVersion!.Length);

        var stored = await Db.CharacterPortraits.AsNoTracking()
            .FirstOrDefaultAsync(p => p.CharacterId == c.Id);

        Assert.NotNull(stored);
        Assert.Equal(StubImageHandler.Png, stored.Bytes);
        Assert.Equal("image/png", stored.ContentType);
        Assert.Equal(Version(StubImageHandler.Png), stored.Version);
        Assert.Equal(1, Images.Calls);
    }

    [Fact]
    public async Task A_character_with_no_portrait_has_no_path_and_costs_no_request()
    {
        var added = await _service.AddAsync(Member, HeraldGameId, "Enchantress", default);

        Assert.True(added.Ok, added.Error);
        Assert.Null(added.Character!.PortraitVersion);
        Assert.Null(added.Character.PortraitPath);
        Assert.Equal(0, Images.Calls);
    }

    [Fact]
    public async Task A_refresh_fetches_the_picture_and_writes_nothing_when_it_is_the_same()
    {
        // It does fetch. Asking the herald whether the picture changed and believing
        // the answer is what broke: the FFXI herald served two different renders
        // under one appearance hash. One image fetch per character per pass is the
        // price of never showing yesterday's armour.
        var added = await _service.AddAsync(Member, HeraldGameId, "Sable", default);
        Assert.True(added.Ok, added.Error);
        Assert.Equal(1, Images.Calls);

        var before = await Db.CharacterPortraits.AsNoTracking()
            .FirstAsync(p => p.CharacterId == added.Character!.Id);

        Assert.True(await _service.RefreshAsync(added.Character!, default));
        await Db.SaveChangesAsync();

        Assert.Equal(2, Images.Calls);

        // Same bytes, so nothing moved: not the version, not the URL a browser has
        // already cached for a year, and not the fetch time.
        var after = await Db.CharacterPortraits.AsNoTracking()
            .FirstAsync(p => p.CharacterId == added.Character!.Id);

        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.FetchedAt, after.FetchedAt);
        Assert.Equal(before.Version, added.Character!.PortraitVersion);
    }

    [Fact]
    public async Task A_new_version_replaces_the_stored_picture()
    {
        var added = await _service.AddAsync(Member, HeraldGameId, "Sable", default);
        Assert.True(added.Ok, added.Error);
        var id = added.Character!.Id;

        var wasVersion = added.Character!.PortraitVersion;

        // The reported bug, exactly. The character changed gear and the herald
        // re-rendered, and the herald says nothing at all about that: same URL,
        // same appearance hash, same immutable ETag. Only the bytes are different.
        Images.Body = [.. StubImageHandler.Png, 0x00];

        Assert.True(await _service.RefreshAsync(added.Character!, default));
        await Db.SaveChangesAsync();

        Assert.Equal(2, Images.Calls);

        var stored = await Db.CharacterPortraits.AsNoTracking().FirstAsync(p => p.CharacterId == id);
        Assert.Equal(Version(Images.Body), stored.Version);
        Assert.NotEqual(wasVersion, stored.Version);
        Assert.Equal(Images.Body, stored.Bytes);
        // And the page points somewhere new, so a browser holding the old one for a
        // year asks again.
        Assert.Equal(stored.Version, added.Character.PortraitVersion);
        // One row per character, not one per version.
        Assert.Equal(1, await Db.CharacterPortraits.CountAsync(p => p.CharacterId == id));
    }

    [Fact]
    public async Task A_renderer_that_is_down_keeps_the_old_picture_and_is_not_an_error()
    {
        var added = await _service.AddAsync(Member, HeraldGameId, "Sable", default);
        Assert.True(added.Ok, added.Error);
        var id = added.Character!.Id;

        Images.ForcedStatus = System.Net.HttpStatusCode.ServiceUnavailable;

        Assert.True(await _service.RefreshAsync(added.Character!, default));
        await Db.SaveChangesAsync();

        // The version stays on what we actually hold, so the path keeps pointing at
        // bytes that exist.
        Assert.Equal(Version(StubImageHandler.Png), added.Character.PortraitVersion);
        var stored = await Db.CharacterPortraits.AsNoTracking().FirstAsync(p => p.CharacterId == id);
        Assert.Equal(Version(StubImageHandler.Png), stored.Version);
        Assert.Equal(StubImageHandler.Png, stored.Bytes);

        // A portrait is decoration. Failing to fetch one must not make a character
        // look stale on the page.
        Assert.Null(added.Character.LastError);
    }

    [Fact]
    public async Task Something_that_is_not_an_image_is_refused()
    {
        // The endpoint echoes the stored content type, so text/html from a herald
        // would be stored cross-site scripting wearing an img tag.
        Images.ContentType = "text/html";

        var added = await _service.AddAsync(Member, HeraldGameId, "Sable", default);

        Assert.True(added.Ok, added.Error);
        Assert.Null(added.Character!.PortraitVersion);
        Assert.False(await Db.CharacterPortraits.AnyAsync(p => p.CharacterId == added.Character.Id));
    }

    [Fact]
    public async Task A_portrait_larger_than_the_cap_is_refused()
    {
        Images.Body = new byte[HeraldFetcher.MaxImageBytes + 1];

        var added = await _service.AddAsync(Member, HeraldGameId, "Sable", default);

        Assert.True(added.Ok, added.Error);
        Assert.Null(added.Character!.PortraitVersion);
    }

    [Fact]
    public async Task Removing_a_character_removes_its_portrait()
    {
        var added = await _service.AddAsync(Member, HeraldGameId, "Sable", default);
        Assert.True(added.Ok, added.Error);
        var id = added.Character!.Id;

        Db.Characters.Remove(added.Character);
        await Db.SaveChangesAsync();

        // By cascade, not by remembering to do it in the handler.
        Assert.False(await Db.CharacterPortraits.AnyAsync(p => p.CharacterId == id));
    }

    [Fact]
    public void Two_pictures_are_two_versions_and_one_picture_is_one()
    {
        // The version decides whether the stored bytes are replaced and what URL a
        // browser is sent to, so a stable mapping is the whole property.
        var png = StubImageHandler.Png;
        byte[] other = [.. png, 0x00];

        Assert.Equal(Version(png), Version(png));
        Assert.NotEqual(Version(png), Version(other));
        Assert.Equal(16, Version(png).Length);

        // A single flipped byte is a different picture. Nothing here rounds.
        byte[] nudged = [.. png];
        nudged[^1] ^= 0x01;
        Assert.NotEqual(Version(png), Version(nudged));
    }
}

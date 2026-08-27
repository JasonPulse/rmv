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
[Trait("Category", "Database")]
[Collection(NetworkCollection.Name)]
public class CharacterServiceTests : IAsyncLifetime
{
    private RmvDbContext _db = null!;
    private CharacterService _service = null!;
    private FakeHeraldAdapter _herald = null!;
    private Member _member = null!;
    private int _blackthornId;
    private int _ffxiId;
    private int _noHeraldId;

    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("RMV_TEST_POSTGRES");

    public async Task InitializeAsync()
    {
        // These are opt-in, so a missing connection string is a clear instruction
        // rather than a silent pass that hides broken coverage.
        if (ConnectionString is null)
        {
            throw new InvalidOperationException(
                "Set RMV_TEST_POSTGRES to run the Network tests, e.g. "
                + "Host=db;Port=5432;Database=rmv;Username=rmv;Password=...");
        }

        _db = new RmvDbContext(new DbContextOptionsBuilder<RmvDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);

        await _db.Database.MigrateAsync();

        _herald = new FakeHeraldAdapter()
            .WithCharacter("Enchantress")
            .WithCharacter("Balder", b => b.Realm = "Midgard")
            .WithCharacter("Fetva")
            .WithCharacter("Teagan");

        _service = new CharacterService(
            _db, new HeraldRegistry([_herald]), NullLogger<CharacterService>.Instance);

        // A member and two games wired to real heralds.
        _member = new Member
        {
            DiscordId = $"test-{Guid.NewGuid():N}"[..24],
            DisplayName = "Test Member",
            Status = MemberStatus.Approved,
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        _db.Members.Add(_member);

        var bt = new GamePresence
        {
            Game = $"BT test {Guid.NewGuid():N}"[..20],
            Guilds = "RMV",
            HeraldAdapterKey = "fake",
            HeraldBaseUrl = "https://fake.test",
        };
        var xi = new GamePresence
        {
            Game = $"XI test {Guid.NewGuid():N}"[..20],
            Guilds = "RMV",
            HeraldAdapterKey = "fake",
            HeraldBaseUrl = "https://fake.test",
        };
        // And one with no herald at all, which is most of the guild's history:
        // servers that never ran one, or no longer do.
        var manual = new GamePresence
        {
            Game = $"No herald {Guid.NewGuid():N}"[..20],
            Guilds = "RMV",
        };

        _db.GamePresences.AddRange(bt, xi, manual);
        await _db.SaveChangesAsync();

        _blackthornId = bt.Id;
        _ffxiId = xi.Id;
        _noHeraldId = manual.Id;
    }

    public async Task DisposeAsync()
    {
        if (_db is null)
        {
            return;
        }

        // Cascades clear the characters.
        _db.Members.Remove(_member);
        _db.GamePresences.RemoveRange(
            _db.GamePresences.Where(g => g.Id == _blackthornId
                                         || g.Id == _ffxiId
                                         || g.Id == _noHeraldId));
        await _db.SaveChangesAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Adds_a_character_with_the_stats_the_herald_gave()
    {
        var outcome = await _service.AddAsync(_member, _blackthornId, "Enchantress", default);

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
        var outcome = await _service.AddAsync(_member, _blackthornId, "enchantress", default);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal("Enchantress", outcome.Character!.Name);
    }

    [Fact]
    public async Task A_herald_that_is_down_does_not_blank_existing_stats()
    {
        var added = await _service.AddAsync(_member, _blackthornId, "Balder", default);
        Assert.True(added.Ok, added.Error);
        var c = added.Character!;

        _herald.ForcedError = "Herald returned 503.";
        var ok = await _service.RefreshAsync(c, default);
        await _db.SaveChangesAsync();

        Assert.False(ok);
        // The stats from the good fetch survive; only the error is new.
        Assert.Equal(50, c.Level);
        Assert.Equal("Midgard", c.Realm);
        Assert.Contains("503", c.LastError!);
    }

    [Fact]
    public async Task A_name_the_herald_does_not_know_saves_nothing()
    {
        var before = await _db.Characters.CountAsync();

        var outcome = await _service.AddAsync(_member, _blackthornId, "Nobodyhere", default);

        Assert.False(outcome.Ok);
        Assert.Contains("no character", outcome.Error!, StringComparison.OrdinalIgnoreCase);
        // The point: a typo must not leave a junk row behind.
        Assert.Equal(before, await _db.Characters.CountAsync());
    }

    [Fact]
    public async Task The_same_character_cannot_be_added_twice()
    {
        var first = await _service.AddAsync(_member, _blackthornId, "Balder", default);
        Assert.True(first.Ok, first.Error);

        var second = await _service.AddAsync(_member, _blackthornId, "Balder", default);

        Assert.False(second.Ok);
        Assert.Contains("already", second.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Claiming_is_case_insensitive()
    {
        var first = await _service.AddAsync(_member, _blackthornId, "Fetva", default);
        Assert.True(first.Ok, first.Error);

        // "fetva" is the same character as "Fetva".
        var second = await _service.AddAsync(_member, _blackthornId, "fetva", default);

        Assert.False(second.Ok);
        Assert.Contains("already", second.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refresh_updates_the_stats_and_clears_the_error()
    {
        var added = await _service.AddAsync(_member, _blackthornId, "Teagan", default);
        Assert.True(added.Ok, added.Error);

        var c = added.Character!;
        c.LastError = "something stale";
        c.Level = 1;
        await _db.SaveChangesAsync();

        var ok = await _service.RefreshAsync(c, default);
        await _db.SaveChangesAsync();

        Assert.True(ok);
        Assert.Equal(50, c.Level);
        Assert.Null(c.LastError);
    }

    // --- games with no herald ------------------------------------------------

    [Fact]
    public async Task Records_a_hand_typed_sheet_for_a_game_with_no_herald()
    {
        var outcome = await _service.AddManualAsync(
            _member, _noHeraldId, "Sigrun", "Warden", 44, default);

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
        Assert.Equal(0, _herald.Calls);
    }

    [Fact]
    public async Task A_sheet_may_leave_the_job_and_level_blank()
    {
        // Fifteen years on, plenty of these are a name and nothing else.
        var outcome = await _service.AddManualAsync(
            _member, _noHeraldId, "Halvard", null, null, default);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Null(outcome.Character!.Class);
        Assert.Null(outcome.Character.Level);
    }

    [Fact]
    public async Task Refusing_a_sheet_for_a_game_that_has_a_herald()
    {
        // Two ways to fill one row means the next refresh discards what was typed.
        var outcome = await _service.AddManualAsync(
            _member, _blackthornId, "Enchantress", "Champion", 50, default);

        Assert.False(outcome.Ok);
        Assert.Contains("looked up", outcome.Error);
    }

    [Fact]
    public async Task Refusing_a_herald_lookup_for_a_game_that_has_none()
    {
        var outcome = await _service.AddAsync(_member, _noHeraldId, "Sigrun", default);

        Assert.False(outcome.Ok);
        Assert.Contains("typed in by hand", outcome.Error);
    }

    [Fact]
    public async Task Refreshing_a_sheet_does_not_mark_it_stale()
    {
        // The reported shape of this bug: a perfectly good sheet showing a "last
        // refresh failed" warning, because nothing was ever going to refresh it.
        var added = await _service.AddManualAsync(
            _member, _noHeraldId, "Sigrun", "Warden", 44, default);
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
            _member, _noHeraldId, "Sigrun", "Warden", 44, default);
        Assert.True(added.Ok, added.Error);
        var id = added.Character!.Id;

        var edited = await _service.UpdateManualAsync(
            added.Character, "Sigrunn", "Druid", 50, default);

        Assert.True(edited.Ok, edited.Error);
        Assert.Equal(id, edited.Character!.Id);

        var reread = await _db.Characters.AsNoTracking().FirstAsync(c => c.Id == id);
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
            _member, _noHeraldId, "Sigrun", "Warden", 44, default);
        Assert.True(added.Ok, added.Error);

        var edited = await _service.UpdateManualAsync(
            added.Character!, "Sigrun", "Warden", 45, default);

        Assert.True(edited.Ok, edited.Error);
        Assert.Equal(45, edited.Character!.Level);
    }

    [Fact]
    public async Task A_sheet_cannot_be_renamed_over_someone_elses_character()
    {
        var mine = await _service.AddManualAsync(_member, _noHeraldId, "Sigrun", null, null, default);
        var theirs = await _service.AddManualAsync(_member, _noHeraldId, "Halvard", null, null, default);
        Assert.True(mine.Ok, mine.Error);
        Assert.True(theirs.Ok, theirs.Error);

        var edited = await _service.UpdateManualAsync(mine.Character!, "Halvard", null, null, default);

        Assert.False(edited.Ok);
        Assert.Contains("already added", edited.Error);
    }

    [Fact]
    public async Task A_herald_character_is_not_hand_editable()
    {
        var added = await _service.AddAsync(_member, _blackthornId, "Fetva", default);
        Assert.True(added.Ok, added.Error);

        var edited = await _service.UpdateManualAsync(added.Character!, "Fetva", "Wizard", 1, default);

        Assert.False(edited.Ok);
        Assert.Contains("Refresh it", edited.Error);
        Assert.Equal("Champion", added.Character!.Class);
    }

    [Fact]
    public async Task A_level_outside_the_range_is_refused()
    {
        Assert.False((await _service.AddManualAsync(
            _member, _noHeraldId, "Sigrun", "Warden", 0, default)).Ok);
        Assert.False((await _service.AddManualAsync(
            _member, _noHeraldId, "Sigrun", "Warden", 1000, default)).Ok);

        // Nothing was written by either attempt.
        Assert.False(await _db.Characters.AnyAsync(c => c.GamePresenceId == _noHeraldId));
    }

    [Fact]
    public async Task An_existing_herald_character_stays_a_herald_character()
    {
        // The migration backfills Source for rows added before it existed, and
        // Herald is the truthful value: every one of them was fetched.
        var added = await _service.AddAsync(_member, _blackthornId, "Teagan", default);

        Assert.True(added.Ok, added.Error);
        Assert.Equal(CharacterSource.Herald, added.Character!.Source);
        Assert.False(added.Character.IsManual);
    }
}

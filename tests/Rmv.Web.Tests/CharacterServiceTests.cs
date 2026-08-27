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
        _db.GamePresences.AddRange(bt, xi);
        await _db.SaveChangesAsync();

        _blackthornId = bt.Id;
        _ffxiId = xi.Id;
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
            _db.GamePresences.Where(g => g.Id == _blackthornId || g.Id == _ffxiId));
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
    public async Task A_game_with_no_herald_says_so_rather_than_failing_oddly()
    {
        var bare = new GamePresence { Game = $"Bare {Guid.NewGuid():N}"[..18], Guilds = "RMV" };
        _db.GamePresences.Add(bare);
        await _db.SaveChangesAsync();

        var outcome = await _service.AddAsync(_member, bare.Id, "Anything", default);

        Assert.False(outcome.Ok);
        Assert.Contains("no herald configured", outcome.Error!, StringComparison.OrdinalIgnoreCase);

        _db.GamePresences.Remove(bare);
        await _db.SaveChangesAsync();
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
}

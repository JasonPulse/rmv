using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Rmv.Web.Data;
using Rmv.Web.Herald;

namespace Rmv.Web.Tests;

/// <summary>
/// The regression guard.
///
/// Portraits shipped with a migration that deliberately cleared the old image
/// URLs, and nothing filled them back in, so every existing character lost its
/// picture and the only route back was opening /characters and pressing refresh
/// once per character. It looked exactly like a broken feature because from the
/// outside it was one.
///
/// So these tests assert the thing that was missing: a character with a herald and
/// no stored portrait ends a backfill pass with one, without anyone pressing
/// anything.
///
/// Against a real Postgres, because the pass selects rows in SQL, and against a
/// fake herald, because these are someone else's servers.
/// </summary>
public class HeraldRefreshServiceTests : HeraldDatabaseTests
{
    private ServiceProvider _services = null!;
    private DatabaseState _state = null!;

    protected override void ConfigureHerald(FakeHeraldAdapter herald) => herald
        .WithCharacter("Sable", b => b.Portrait =
            new HeraldPortrait("https://fake.test/portraits/1.png?v=aaa", "aaa"))
        .WithCharacter("Balder", b => b.Portrait =
            new HeraldPortrait("https://fake.test/portraits/2.png?v=bbb", "bbb"))
        .WithCharacter("Plain");

    protected override Task SeedAsync()
    {
        _state = new DatabaseState(DatabaseStatus.Ready);

        // The real graph, wired as Program.cs wires it, so the pass resolves a
        // scoped DbContext and CharacterService exactly as it does in the app.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<RmvDbContext>(o => o.UseNpgsql(ConnectionString));
        services.AddSingleton(_state);
        services.AddSingleton(Fetcher);
        services.AddScoped<IHeraldAdapter>(_ => Herald);
        services.AddScoped<HeraldRegistry>();
        services.AddScoped<CharacterService>();
        _services = services.BuildServiceProvider();

        return Task.CompletedTask;
    }

    protected override async ValueTask DisposeExtraAsync() => await _services.DisposeAsync();

    /// <summary>No pause: the two second politeness delay is not the thing under test.</summary>
    private HeraldRefreshService Service() =>
        new(_services.GetRequiredService<IServiceScopeFactory>(),
            _state,
            NullLogger<HeraldRefreshService>.Instance)
        {
            BetweenCharacters = TimeSpan.Zero,
        };

    /// <summary>Exactly the state the portraits migration left every character in.</summary>
    private async Task<Character> PostMigrationCharacterAsync(string name, int? gameId = null)
    {
        var c = new Character
        {
            MemberId = Member.Id,
            GamePresenceId = gameId ?? HeraldGameId,
            Name = name,
            Source = CharacterSource.Herald,
            AddedAt = DateTimeOffset.UtcNow,
            PortraitVersion = null,
        };

        Db.Characters.Add(c);
        await Db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task The_backfill_gives_a_character_its_picture_without_anyone_pressing_refresh()
    {
        var c = await PostMigrationCharacterAsync("Sable");

        var summary = await Service().RunAsync(missingPortraitsOnly: true, default);

        Assert.Equal(1, summary.Refreshed);
        Assert.Equal(0, summary.Failed);

        var after = await Db.Characters.AsNoTracking().FirstAsync(x => x.Id == c.Id);
        Assert.NotNull(after.PortraitVersion);
        Assert.Equal($"/characters/{c.Id}/portrait?v={after.PortraitVersion}", after.PortraitPath);

        var bytes = await Db.CharacterPortraits.AsNoTracking()
            .Where(p => p.CharacterId == c.Id)
            .Select(p => p.Bytes)
            .FirstOrDefaultAsync();

        Assert.Equal(StubImageHandler.Png, bytes);
    }

    [Fact]
    public async Task The_backfill_skips_characters_that_already_have_one()
    {
        var already = await PostMigrationCharacterAsync("Sable");
        await Service().RunAsync(missingPortraitsOnly: true, default);
        Assert.Equal(1, Images.Calls);

        // A second backfill has nothing to do, which is what makes it safe to run
        // on every startup.
        var summary = await Service().RunAsync(missingPortraitsOnly: true, default);

        Assert.Equal(0, summary.Total);
        Assert.Equal(1, Images.Calls);
        Assert.NotNull(await Db.Characters.AsNoTracking()
            .Where(c => c.Id == already.Id).Select(c => c.PortraitVersion).FirstAsync());
    }

    [Fact]
    public async Task The_backfill_covers_every_character_that_is_missing_one()
    {
        await PostMigrationCharacterAsync("Sable");
        await PostMigrationCharacterAsync("Balder");

        var summary = await Service().RunAsync(missingPortraitsOnly: true, default);

        Assert.Equal(2, summary.Refreshed);
        Assert.Equal(2, await Db.CharacterPortraits
            .CountAsync(p => Db.Characters.Any(c => c.Id == p.CharacterId && c.GamePresenceId == HeraldGameId)));
    }

    [Fact]
    public async Task A_hand_typed_character_is_never_touched()
    {
        // No herald to ask, and the row is the owner's own text. A pass that
        // "refreshed" it would be overwriting someone's work.
        var manual = new Character
        {
            MemberId = Member.Id,
            GamePresenceId = NoHeraldGameId,
            Name = "Grimwald",
            Class = "Shadow Knight",
            Level = 60,
            Source = CharacterSource.Manual,
            AddedAt = DateTimeOffset.UtcNow,
        };
        Db.Characters.Add(manual);
        await Db.SaveChangesAsync();

        var summary = await Service().RunAsync(missingPortraitsOnly: true, default);

        Assert.Equal(0, summary.Total);
        Assert.Equal(0, Herald.Calls);

        var after = await Db.Characters.AsNoTracking().FirstAsync(c => c.Id == manual.Id);
        Assert.Equal("Shadow Knight", after.Class);
        Assert.Equal(60, after.Level);
        Assert.Null(after.LastError);
    }

    [Fact]
    public async Task A_herald_character_on_a_game_with_no_adapter_is_skipped()
    {
        // Left behind when an admin clears a game's herald. There is nothing to
        // ask, so the pass must not count it as a failure every day forever.
        await PostMigrationCharacterAsync("Sable", NoHeraldGameId);

        var summary = await Service().RunAsync(missingPortraitsOnly: true, default);

        Assert.Equal(0, summary.Total);
    }

    [Fact]
    public async Task A_herald_that_is_down_leaves_the_previous_data_and_keeps_going()
    {
        var first = await PostMigrationCharacterAsync("Sable");
        var second = await PostMigrationCharacterAsync("Balder");

        Herald.ForcedError = "Herald returned 503.";

        var summary = await Service().RunAsync(missingPortraitsOnly: true, default);

        // Both attempted, both failed, and the pass did not stop at the first.
        Assert.Equal(0, summary.Refreshed);
        Assert.Equal(2, summary.Failed);

        foreach (var id in new[] { first.Id, second.Id })
        {
            var after = await Db.Characters.AsNoTracking().FirstAsync(c => c.Id == id);
            Assert.Null(after.PortraitVersion);
            Assert.NotNull(after.LastError);
        }
    }

    [Fact]
    public async Task The_daily_pass_refreshes_stats_as_well_and_not_only_the_missing()
    {
        var c = await PostMigrationCharacterAsync("Sable");
        await Service().RunAsync(missingPortraitsOnly: true, default);

        // Stats moved on the herald since it was added.
        Herald.Known["Sable"] = Herald.Known["Sable"] with { Level = 50, Class = "Champion" };

        var summary = await Service().RunAsync(missingPortraitsOnly: false, default);

        Assert.Equal(1, summary.Refreshed);
        var after = await Db.Characters.AsNoTracking().FirstAsync(x => x.Id == c.Id);
        Assert.Equal(50, after.Level);
        Assert.Equal("Champion", after.Class);
        // The picture had not changed, so it was not downloaded again.
        Assert.Equal(1, Images.Calls);
    }

    [Fact]
    public async Task Nothing_runs_while_the_database_is_not_ready()
    {
        await PostMigrationCharacterAsync("Sable");
        _state.Set(DatabaseStatus.Failed, "down");

        var summary = await Service().RunAsync(missingPortraitsOnly: true, default);

        Assert.Equal(0, summary.Total);
        Assert.Equal(0, Herald.Calls);
    }
}

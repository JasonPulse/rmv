using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Rmv.Web.Data;
using Rmv.Web.Herald;

namespace Rmv.Web.Tests;

/// <summary>
/// The fixture every herald test against a real Postgres needs: a member, games
/// with and without a herald, a fake herald, a stub for image fetches, and a
/// teardown that takes it all away again.
///
/// Extracted because two test classes had grown their own copy of it and the
/// copies were already drifting. Test setup is code, and one source of truth
/// applies to it too.
///
/// Needs RMV_TEST_POSTGRES, so everything here is tagged Database and excluded
/// from CI. Rows are named with a fresh guid per run, so two runs against the same
/// database cannot collide.
/// </summary>
[Trait("Category", "Database")]
[Collection(NetworkCollection.Name)]
public abstract class HeraldDatabaseTests : IAsyncLifetime
{
    protected RmvDbContext Db { get; private set; } = null!;

    protected Member Member { get; private set; } = null!;

    /// <summary>A game wired to the fake herald.</summary>
    protected int HeraldGameId { get; private set; }

    /// <summary>A game with no herald at all, which is most of the guild's history.</summary>
    protected int NoHeraldGameId { get; private set; }

    protected FakeHeraldAdapter Herald { get; private set; } = null!;

    protected StubImageHandler Images { get; private set; } = null!;

    protected HeraldFetcher Fetcher { get; private set; } = null!;

    private readonly List<int> _gameIds = [];

    protected static string ConnectionString =>
        Environment.GetEnvironmentVariable("RMV_TEST_POSTGRES")
        ?? throw new InvalidOperationException(
            "Set RMV_TEST_POSTGRES to run Database tests, e.g. "
            + "Host=localhost;Port=5432;Database=rmv_test;Username=rmv;Password=...");

    /// <summary>Characters the fake herald should know about.</summary>
    protected abstract void ConfigureHerald(FakeHeraldAdapter herald);

    /// <summary>Anything else a concrete class needs, after the shared seed.</summary>
    protected virtual Task SeedAsync() => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        Herald = new FakeHeraldAdapter();
        ConfigureHerald(Herald);

        Images = new StubImageHandler();
        Fetcher = new HeraldFetcher(new HttpClient(Images), NullLogger<HeraldFetcher>.Instance);

        Db = new RmvDbContext(new DbContextOptionsBuilder<RmvDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);

        await Db.Database.MigrateAsync();

        Member = new Member
        {
            DiscordId = $"test-{Guid.NewGuid():N}"[..24],
            DisplayName = "Test Member",
            Status = MemberStatus.Approved,
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        Db.Members.Add(Member);
        await Db.SaveChangesAsync();

        HeraldGameId = await NewGameAsync(withHerald: true);
        NoHeraldGameId = await NewGameAsync(withHerald: false);

        await SeedAsync();
    }

    /// <summary>
    /// Another game, registered for cleanup. For a test that needs two herald
    /// games, such as one checking a name claimed on a different game.
    /// </summary>
    protected async Task<int> NewGameAsync(bool withHerald)
    {
        var game = new GamePresence
        {
            Game = $"T {Guid.NewGuid():N}"[..20],
            Guilds = "RMV",
            HeraldAdapterKey = withHerald ? Herald.Key : null,
            HeraldBaseUrl = withHerald ? Herald.DefaultBaseUrl : null,
        };

        Db.GamePresences.Add(game);
        await Db.SaveChangesAsync();
        _gameIds.Add(game.Id);

        return game.Id;
    }

    public async Task DisposeAsync()
    {
        if (Db is null)
        {
            return;
        }

        // Cascades clear the characters and their portraits.
        Db.Members.Remove(Member);
        Db.GamePresences.RemoveRange(Db.GamePresences.Where(g => _gameIds.Contains(g.Id)));
        await Db.SaveChangesAsync();
        await Db.DisposeAsync();

        await DisposeExtraAsync();
    }

    protected virtual ValueTask DisposeExtraAsync() => ValueTask.CompletedTask;
}

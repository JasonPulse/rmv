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
    private readonly List<int> _memberIds = [];

    protected static string ConnectionString =>
        Environment.GetEnvironmentVariable("RMV_TEST_POSTGRES")
        ?? throw new InvalidOperationException(
            "Set RMV_TEST_POSTGRES to run Database tests, e.g. "
            + "Host=localhost;Port=5432;Database=rmv_test;Username=rmv;Password=...");

    /// <summary>
    /// The repository root, for tests that need the app's own files: the signature
    /// fonts and the preset backgrounds.
    ///
    /// Here rather than in each test class, because two of them had grown the same
    /// walk and this base class exists for exactly that.
    /// </summary>
    protected static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Rmv.Web")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        return dir.FullName;
    }

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
        _memberIds.Add(Member.Id);

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

    /// <summary>
    /// A second member, registered for cleanup.
    ///
    /// For a test about one member not being able to touch another's rows, which is
    /// the check worth having on anything member-owned.
    /// </summary>
    protected async Task<Member> NewMemberAsync(
        string name = "Someone Else", MemberStatus status = MemberStatus.Approved)
    {
        var member = new Member
        {
            DiscordId = $"test-{Guid.NewGuid():N}"[..24],
            DisplayName = name,
            Status = status,
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        Db.Members.Add(member);
        await Db.SaveChangesAsync();
        _memberIds.Add(member.Id);

        return member;
    }

    /// <summary>
    /// A screenshot row with bytes, owned by whoever is given.
    ///
    /// Here rather than in each test class for the reason this base class exists at
    /// all: two of them had grown their own copy and the copies were drifting.
    /// </summary>
    protected static Screenshot NewScreenshot(int memberId, string caption) => new()
    {
        MemberId = memberId,
        Caption = caption,
        ContentType = "image/png",
        Width = 1,
        Height = 1,
        Bytes = 1,
        UploadedAt = DateTimeOffset.UtcNow,
        Image = new ScreenshotImage { Bytes = [1] },
    };

    public async Task DisposeAsync()
    {
        if (Db is null)
        {
            return;
        }

        // Before the context goes, not after. A hook that runs once the DbContext is
        // disposed cannot clean up any row, which is a trap rather than a hook.
        await DisposeExtraAsync();

        // Cascades clear the characters, portraits and screenshots.
        Db.Members.RemoveRange(Db.Members.Where(m => _memberIds.Contains(m.Id)));
        Db.GamePresences.RemoveRange(Db.GamePresences.Where(g => _gameIds.Contains(g.Id)));
        await Db.SaveChangesAsync();
        await Db.DisposeAsync();
    }

    protected virtual ValueTask DisposeExtraAsync() => ValueTask.CompletedTask;
}

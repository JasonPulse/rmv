using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Rmv.Web.Data;

namespace Rmv.Web.Tests;

/// <summary>
/// Access as the application actually assembles it: real rows, the real
/// MemberDirectory, and the real authorization handlers.
///
/// The fold itself is covered offline in AccessTests. What needs a database is the
/// wiring, and one case in particular. A root admin whose row said Pending passed
/// every policy while the gallery and the profile refused them, because the
/// policies read configuration first and those pages read the row alone. Both
/// readings are gone; this proves the one that replaced them gives the answer the
/// site needs for every shape a row can be in.
/// </summary>
[Trait("Category", "Database")]
[Collection(NetworkCollection.Name)]
public class AccessDatabaseTests : IAsyncLifetime
{
    private RmvDbContext _db = null!;
    private readonly List<string> _created = [];

    private const string SomeoneElse = "999888777666555444";

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("RMV_TEST_POSTGRES")
        ?? throw new InvalidOperationException("Set RMV_TEST_POSTGRES to run Database tests.");

    public async Task InitializeAsync()
    {
        _db = new RmvDbContext(new DbContextOptionsBuilder<RmvDbContext>()
            .UseNpgsql(ConnectionString).Options);

        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        _db.Members.RemoveRange(_db.Members.Where(m => _created.Contains(m.DiscordId)));
        await _db.SaveChangesAsync();
        await _db.DisposeAsync();
    }

    private static IConfiguration Config(string? rootIds) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("Admin:DiscordIds", rootIds)])
            .Build();

    private static ClaimsPrincipal SignedIn(string id) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "TestAuth"));

    /// <summary>The one access authority, wired to the real directory.</summary>
    private CurrentMember Access(IConfiguration config) =>
        new(config,
            NullLogger<CurrentMember>.Instance,
            new MemberDirectory(_db, config, NullLogger<MemberDirectory>.Instance));

    private static async Task<bool> AdminPolicyAsync(CurrentMember me, ClaimsPrincipal user)
    {
        var requirement = new AdminRequirement();
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await new AdminAuthorizationHandler(me).HandleAsync(context);

        return context.HasSucceeded;
    }

    private static async Task<bool> ContributorPolicyAsync(CurrentMember me, ClaimsPrincipal user)
    {
        var requirement = new ApprovedMemberRequirement();
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await new ApprovedMemberAuthorizationHandler(me).HandleAsync(context);

        return context.HasSucceeded;
    }

    /// <summary>A row in exactly the state asked for, written without the directory.</summary>
    private async Task<string> RowAsync(MemberStatus status, bool isAdmin)
    {
        var id = $"t{Guid.NewGuid():N}"[..20];
        _created.Add(id);

        _db.Members.Add(new Member
        {
            DiscordId = id,
            DisplayName = "Someone",
            Status = status,
            IsAdmin = isAdmin,
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();

        return id;
    }

    [Theory]
    // root,  status,                    isAdmin, administer, contribute
    [InlineData(false, MemberStatus.Pending, false, false, false)]
    [InlineData(false, MemberStatus.Pending, true, true, true)]
    [InlineData(false, MemberStatus.Approved, false, false, true)]
    [InlineData(false, MemberStatus.Approved, true, true, true)]
    [InlineData(false, MemberStatus.Blocked, false, false, false)]
    [InlineData(false, MemberStatus.Blocked, true, false, false)]
    // A root admin is everything, whatever the row says. Every one of these six was
    // a way for the site to contradict itself.
    [InlineData(true, MemberStatus.Pending, false, true, true)]
    [InlineData(true, MemberStatus.Pending, true, true, true)]
    [InlineData(true, MemberStatus.Approved, false, true, true)]
    [InlineData(true, MemberStatus.Approved, true, true, true)]
    [InlineData(true, MemberStatus.Blocked, false, true, true)]
    [InlineData(true, MemberStatus.Blocked, true, true, true)]
    public async Task Every_shape_a_row_can_be_in(
        bool root, MemberStatus status, bool isAdmin, bool administer, bool contribute)
    {
        var id = await RowAsync(status, isAdmin);
        var config = Config(root ? id : SomeoneElse);
        var user = SignedIn(id);

        var access = await Access(config).AccessAsync(user);

        Assert.Equal(administer, access.CanAdminister);
        Assert.Equal(contribute, access.CanContribute);
        Assert.Equal(root, access.IsRoot);

        // And the handlers say the same, because they read this and nothing else.
        // A fresh one each time, since the answer is cached per request.
        Assert.Equal(administer, await AdminPolicyAsync(Access(config), user));
        Assert.Equal(contribute, await ContributorPolicyAsync(Access(config), user));
    }

    [Fact]
    public async Task The_row_a_root_admin_is_shown_matches_what_they_may_do()
    {
        // The screen that started this: /admin/members printed "PENDING" next to
        // "ROOT". Nothing derives access from the row now, but the row is what the
        // admin table displays, so it still has to stop lying.
        var id = await RowAsync(MemberStatus.Pending, isAdmin: false);
        var config = Config(id);
        var user = SignedIn(id);

        var access = await Access(config).AccessAsync(user);

        Assert.True(access.CanAdminister);
        Assert.NotNull(access.Member);
        Assert.Equal(MemberStatus.Approved, access.Member.Status);
        Assert.True(access.Member.IsAdmin);
        Assert.Equal("Admin:DiscordIds", access.Member.ApprovedBy);

        var stored = await _db.Members.AsNoTracking().FirstAsync(m => m.DiscordId == id);
        Assert.Equal(MemberStatus.Approved, stored.Status);
        Assert.True(stored.IsAdmin);
    }

    [Fact]
    public async Task A_root_admin_who_has_never_signed_in_gets_a_row_that_says_so()
    {
        var id = $"t{Guid.NewGuid():N}"[..20];
        _created.Add(id);
        var config = Config(id);

        var access = await Access(config).AccessAsync(SignedIn(id));

        Assert.True(access.CanAdminister);
        Assert.True(access.Member!.IsAdmin);
        Assert.Equal(MemberStatus.Approved, access.Member.Status);
    }

    [Fact]
    public async Task An_ordinary_first_sign_in_is_pending_and_may_do_nothing()
    {
        var id = $"t{Guid.NewGuid():N}"[..20];
        _created.Add(id);

        var access = await Access(Config(SomeoneElse)).AccessAsync(SignedIn(id));

        Assert.False(access.CanContribute);
        Assert.False(access.CanAdminister);
        Assert.True(access.Pending);
        Assert.Equal(MemberStatus.Pending, access.Member!.Status);
    }

    [Fact]
    public async Task One_request_reads_the_member_once()
    {
        // Authorization asks, then the masthead asks, then the page body asks. That
        // is one indexed read, not three, and more importantly it is one answer.
        var id = await RowAsync(MemberStatus.Approved, isAdmin: false);
        var me = Access(Config(SomeoneElse));
        var user = SignedIn(id);

        var first = await me.AccessAsync(user);
        var second = await me.AccessAsync(user);
        var member = await me.GetAsync(user);

        Assert.Same(first, second);
        Assert.Same(first.Member, member);
    }
}

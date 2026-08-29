using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Rmv.Web.Data;

namespace Rmv.Web.Tests;

/// <summary>
/// The policy and the row must give the same answer.
///
/// This test exists because a bug got past a duplicate-code detector that reports
/// zero duplication, and it was still two ways of answering one question.
///
/// The site decides "may this person do X" two ways on purpose:
///
///   The policy, which reads Admin:DiscordIds first and the database second. That
///   order is deliberate: a root admin has to work when Postgres is unreachable,
///   which is what stops a bad grant or an outage locking you out of your own site.
///
///   Member.CanContribute and Member.CanAdminister, read straight off the row. The
///   gallery uses these to decide whether to offer an upload and whether to offer
///   removing someone else's screenshot.
///
/// Nothing textual is shared between those, so a detector comparing lines cannot
/// see them as the same question. A root admin whose row said Pending passed every
/// policy and failed every row check, and the site showed them no upload button
/// while letting them into every admin page.
///
/// So the guard is behavioural: walk every combination of root, status and admin
/// flag, ask both, and require the same answer. Nothing here asserts what the
/// answer should be, only that the two agree, which is the property that was broken.
/// </summary>
[Trait("Category", "Database")]
[Collection(NetworkCollection.Name)]
public class AccessAgreementTests : IAsyncLifetime
{
    private RmvDbContext _db = null!;
    private readonly List<string> _created = [];

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

    /// <summary>A provider that hands out the one real DbContext, as the app's scope does.</summary>
    private IServiceProvider Services() =>
        new ServiceCollection().AddSingleton(_db).BuildServiceProvider();

    private static ClaimsPrincipal SignedIn(string id) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "TestAuth"));

    private async Task<bool> PolicySaysAsync(bool admin, IConfiguration config, ClaimsPrincipal user)
    {
        if (admin)
        {
            var requirement = new AdminRequirement();
            var context = new AuthorizationHandlerContext([requirement], user, null);

            await new AdminAuthorizationHandler(
                Services(), config, NullLogger<AdminAuthorizationHandler>.Instance)
                .HandleAsync(context);

            return context.HasSucceeded;
        }

        var approved = new ApprovedMemberRequirement();
        var ctx = new AuthorizationHandlerContext([approved], user, null);

        await new ApprovedMemberAuthorizationHandler(
            Services(), config, NullLogger<ApprovedMemberAuthorizationHandler>.Instance)
            .HandleAsync(ctx);

        return ctx.HasSucceeded;
    }

    /// <summary>
    /// Every shape a member row can be in, crossed with whether configuration names
    /// them as root. Twelve rows, and the two answers have to match on all of them.
    /// </summary>
    public static TheoryData<bool, MemberStatus, bool> Everything()
    {
        var data = new TheoryData<bool, MemberStatus, bool>();

        foreach (var root in new[] { false, true })
        {
            foreach (var status in Enum.GetValues<MemberStatus>())
            {
                foreach (var isAdmin in new[] { false, true })
                {
                    data.Add(root, status, isAdmin);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Everything))]
    public async Task The_policy_and_the_row_agree_on_contributing(
        bool root, MemberStatus status, bool isAdmin)
    {
        var member = await RowAsync(root, status, isAdmin);
        var config = Config(root ? member.DiscordId : "999888777666555444");

        var policy = await PolicySaysAsync(admin: false, config, SignedIn(member.DiscordId));

        Assert.Equal(member.CanContribute, policy);
    }

    [Theory]
    [MemberData(nameof(Everything))]
    public async Task The_policy_and_the_row_agree_on_administering(
        bool root, MemberStatus status, bool isAdmin)
    {
        var member = await RowAsync(root, status, isAdmin);
        var config = Config(root ? member.DiscordId : "999888777666555444");

        var policy = await PolicySaysAsync(admin: true, config, SignedIn(member.DiscordId));

        Assert.Equal(member.CanAdminister, policy);
    }

    /// <summary>
    /// Writes the row through MemberDirectory, which is what the app does on every
    /// request and what keeps a root admin's row matching configuration.
    ///
    /// Going straight to the DbSet would be testing a state the application never
    /// leaves behind, and would fail for a reason nobody has to fix.
    /// </summary>
    private async Task<Member> RowAsync(bool root, MemberStatus status, bool isAdmin)
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

        var config = Config(root ? id : "999888777666555444");
        var directory = new MemberDirectory(_db, config, NullLogger<MemberDirectory>.Instance);

        return (await directory.EnsureAsync(SignedIn(id), default))!;
    }

    [Fact]
    public async Task A_root_admin_whose_row_says_pending_is_the_case_that_was_broken()
    {
        // Written directly, bypassing the directory, so this is the state the site
        // was actually in: root by configuration, Pending in the table.
        var id = $"t{Guid.NewGuid():N}"[..20];
        _created.Add(id);

        _db.Members.Add(new Member
        {
            DiscordId = id,
            DisplayName = "Root",
            Status = MemberStatus.Pending,
            IsAdmin = false,
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();

        var config = Config(id);
        var user = SignedIn(id);

        // The policy lets them in, from configuration, before it reads the table.
        Assert.True(await PolicySaysAsync(admin: true, config, user));

        // The row, at this moment, says no. That is the disagreement, and the
        // gallery believed the row.
        var stale = await _db.Members.AsNoTracking().FirstAsync(m => m.DiscordId == id);
        Assert.False(stale.CanAdminister);

        // One access through the directory is what reconciles them.
        var directory = new MemberDirectory(_db, config, NullLogger<MemberDirectory>.Instance);
        var fixedUp = await directory.EnsureAsync(user, default);

        Assert.True(fixedUp!.CanAdminister);
        Assert.True(fixedUp.CanContribute);
        Assert.Equal(await PolicySaysAsync(admin: true, config, user), fixedUp.CanAdminister);
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rmv.Web.Data;

namespace Rmv.Web.Tests;

/// <summary>
/// Access, as one truth table.
///
/// This is the whole of it. There is one fold from the two sources, Admin:DiscordIds
/// and the member row, to the two answers, and every one of its inputs is covered
/// here without a database because the fold is pure.
///
/// The history is worth keeping. The two answers were written out in three places
/// and the three disagreed, which was fixed by making them one property on Member.
/// Then a root admin's row said Pending: the policies read configuration first and
/// let them in, while the gallery and the profile read the row alone and refused
/// them, and the admin table printed "PENDING" next to "ROOT". That was fixed by
/// reconciling the row with configuration, which was still two answers being kept
/// in step. Now there is one, and this pins it.
/// </summary>
public class AccessTests
{
    private static Member Row(MemberStatus status, bool admin) =>
        new() { DiscordId = "111", DisplayName = "Someone", Status = status, IsAdmin = admin };

    [Theory]
    // status,                          admin, administer, contribute
    [InlineData(MemberStatus.Approved, false, false, true)]
    [InlineData(MemberStatus.Approved, true, true, true)]
    [InlineData(MemberStatus.Pending, false, false, false)]
    // Admin implies contributing: an admin who can edit the site but not add a
    // character is nonsense, and /admin/members approves on promotion anyway.
    [InlineData(MemberStatus.Pending, true, true, true)]
    // Blocked beats admin, both ways. Revoking someone must not depend on
    // remembering to clear the admin flag too.
    [InlineData(MemberStatus.Blocked, false, false, false)]
    [InlineData(MemberStatus.Blocked, true, false, false)]
    public void An_ordinary_member_is_their_row(
        MemberStatus status, bool admin, bool canAdminister, bool canContribute)
    {
        var access = Access.Of("111", Row(status, admin), isRoot: false);

        Assert.Equal(canAdminister, access.CanAdminister);
        Assert.Equal(canContribute, access.CanContribute);
        Assert.False(access.IsRoot);
        Assert.True(access.SignedIn);
    }

    [Theory]
    [InlineData(MemberStatus.Approved, false)]
    [InlineData(MemberStatus.Approved, true)]
    [InlineData(MemberStatus.Pending, false)]
    [InlineData(MemberStatus.Pending, true)]
    // Including Blocked. The application cannot block a root id, because the grant
    // is in configuration: a row saying otherwise is a lie, not a restriction.
    [InlineData(MemberStatus.Blocked, false)]
    [InlineData(MemberStatus.Blocked, true)]
    public void A_root_admin_is_everything_whatever_the_row_says(MemberStatus status, bool admin)
    {
        var access = Access.Of("111", Row(status, admin), isRoot: true);

        Assert.True(access.CanAdminister);
        Assert.True(access.CanContribute);
        Assert.True(access.IsRoot);
    }

    [Fact]
    public void A_root_admin_needs_no_row_at_all()
    {
        // The reason root ids exist. Their access cannot depend on a database read,
        // or an outage locks you out of your own site.
        var access = Access.Of("111", member: null, isRoot: true);

        Assert.True(access.CanAdminister);
        Assert.True(access.CanContribute);
    }

    [Fact]
    public void Anyone_else_without_a_row_may_do_nothing()
    {
        var access = Access.Of("111", member: null, isRoot: false);

        Assert.False(access.CanAdminister);
        Assert.False(access.CanContribute);
        Assert.True(access.SignedIn);
        Assert.True(access.Pending);
    }

    [Fact]
    public void No_discord_id_is_nobody()
    {
        Assert.Same(Access.None, Access.Of(null, Row(MemberStatus.Approved, true), isRoot: true));
        Assert.Same(Access.None, Access.Of("", null, isRoot: true));

        Assert.False(Access.None.SignedIn);
        Assert.False(Access.None.CanContribute);
        Assert.False(Access.None.CanAdminister);
    }

    [Fact]
    public void Administering_always_implies_contributing()
    {
        // Not a coincidence maintained in two branches: contributing is defined in
        // terms of administering.
        foreach (var root in new[] { false, true })
        {
            foreach (var status in Enum.GetValues<MemberStatus>())
            {
                foreach (var admin in new[] { false, true })
                {
                    var access = Access.Of("111", Row(status, admin), root);

                    if (access.CanAdminister)
                    {
                        Assert.True(access.CanContribute,
                            $"root {root}, {status}, admin {admin}: administers but cannot contribute");
                    }
                }
            }
        }
    }

    [Fact]
    public void Pending_and_blocked_are_the_states_the_site_says_out_loud()
    {
        Assert.True(Access.Of("111", Row(MemberStatus.Pending, false), false).Pending);
        Assert.False(Access.Of("111", Row(MemberStatus.Approved, false), false).Pending);

        Assert.True(Access.Of("111", Row(MemberStatus.Blocked, false), false).Blocked);
        Assert.False(Access.Of("111", Row(MemberStatus.Blocked, false), true).Blocked);

        // A blocked member is not waiting on anybody.
        Assert.False(Access.Of("111", Row(MemberStatus.Blocked, false), false).Pending);
    }
}

/// <summary>
/// The authorization handlers, at the points that need no database.
///
/// They decide nothing now: both read one field off the answer
/// CurrentMember.AccessAsync gives. So what is under test is that they read the
/// right field and that the no-database path still lets a root admin in and nobody
/// else, which is the property the site's recovery depends on.
/// </summary>
public class MemberRequirementHandlerTests
{
    private static IConfiguration Config(string? rootIds) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("Admin:DiscordIds", rootIds)])
            .Build();

    /// <summary>No MemberDirectory, which is the site running with no connection string.</summary>
    private static CurrentMember NoDatabase(IConfiguration config) =>
        new(config, NullLogger<CurrentMember>.Instance);

    private static ClaimsPrincipal SignedIn(string? id) =>
        new(new ClaimsIdentity(
            id is null ? [] : [new Claim(ClaimTypes.NameIdentifier, id)],
            "TestAuth"));

    private static async Task<bool> AdminAsync(IConfiguration config, ClaimsPrincipal user)
    {
        var requirement = new AdminRequirement();
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await new AdminAuthorizationHandler(NoDatabase(config)).HandleAsync(context);

        return context.HasSucceeded;
    }

    private static async Task<bool> ContributorAsync(IConfiguration config, ClaimsPrincipal user)
    {
        var requirement = new ApprovedMemberRequirement();
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await new ApprovedMemberAuthorizationHandler(NoDatabase(config)).HandleAsync(context);

        return context.HasSucceeded;
    }

    [Fact]
    public async Task A_root_admin_passes_without_a_database()
    {
        // The whole reason root admins exist: a bad grant or an outage must not
        // lock you out of your own site.
        var config = Config("111222333444555666");
        var user = SignedIn("111222333444555666");

        Assert.True(await AdminAsync(config, user));
        Assert.True(await ContributorAsync(config, user));
    }

    [Fact]
    public async Task Anyone_else_is_denied_when_there_is_no_database()
    {
        var config = Config("111222333444555666");
        var user = SignedIn("999888777666555444");

        Assert.False(await AdminAsync(config, user));
        Assert.False(await ContributorAsync(config, user));
    }

    [Fact]
    public async Task A_principal_with_no_discord_id_is_denied()
    {
        var config = Config("111222333444555666");
        var user = SignedIn(null);

        Assert.False(await AdminAsync(config, user));
        Assert.False(await ContributorAsync(config, user));
    }

    [Fact]
    public async Task An_anonymous_principal_is_denied()
    {
        var config = Config("111222333444555666");

        // Authenticated is what makes a principal signed in. An identity carrying
        // the right id but no authentication type is not one.
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "111222333444555666")]));

        Assert.False(user.Identity!.IsAuthenticated);
        Assert.False(await AdminAsync(config, user));
        Assert.False(await ContributorAsync(config, user));
    }

    [Fact]
    public async Task An_empty_root_list_means_nobody_not_everybody()
    {
        var user = SignedIn("111222333444555666");

        Assert.False(await AdminAsync(Config(null), user));
        Assert.False(await ContributorAsync(Config(""), user));
    }

    [Fact]
    public async Task The_answer_is_resolved_once_per_request()
    {
        // Both handlers run on a request to an admin page, and the masthead asks
        // again while rendering it. If that were three reads, they could differ; the
        // point of caching it is that within one request there is one answer.
        var config = Config("111222333444555666");
        var user = SignedIn("111222333444555666");
        var me = NoDatabase(config);

        var first = await me.AccessAsync(user);
        var second = await me.AccessAsync(user);

        Assert.Same(first, second);
    }

    [Fact]
    public void CurrentMember_resolves_with_no_database_registered()
    {
        // Program.cs registers it outside the database block on purpose, so the
        // handlers can take it directly instead of each null-checking a DbContext.
        // This fails if MemberDirectory stops being an optional parameter.
        var services = new ServiceCollection();
        services.AddSingleton(Config("111"));
        services.AddLogging();
        services.AddScoped<CurrentMember>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CurrentMember>());
    }
}

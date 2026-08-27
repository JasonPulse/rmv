using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Rmv.Web.Data;

namespace Rmv.Web.Tests;

/// <summary>
/// The two questions authorisation asks of a member, as a truth table.
///
/// These were written out three times in three places and the three disagreed:
/// the policy handler counted admins as contributors, Member.CanContribute did
/// not, and the profile page counted only root admins. A database admin still
/// marked Pending therefore passed the policy while their own profile told them
/// they could not contribute. Now there is one answer, and this is it.
/// </summary>
public class MemberRuleTests
{
    private static Member M(MemberStatus status, bool admin) =>
        new() { DisplayName = "Someone", Status = status, IsAdmin = admin };

    [Theory]
    // status,                  admin, administer, contribute
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
    public void The_whole_truth_table(
        MemberStatus status, bool admin, bool canAdminister, bool canContribute)
    {
        var member = M(status, admin);

        Assert.Equal(canAdminister, member.CanAdminister);
        Assert.Equal(canContribute, member.CanContribute);
    }

    [Fact]
    public void Administering_implies_contributing()
    {
        // Not a coincidence to be maintained in two places: CanContribute is
        // defined in terms of CanAdminister.
        foreach (var status in Enum.GetValues<MemberStatus>())
        {
            foreach (var admin in new[] { true, false })
            {
                var member = M(status, admin);
                if (member.CanAdminister)
                {
                    Assert.True(member.CanContribute,
                        $"{status}/{admin} may administer but not contribute");
                }
            }
        }
    }
}

/// <summary>
/// The handler shape both policies share, at the points that do not need a
/// database: a principal with no id, a root admin, and no database at all.
///
/// Fails closed is the property under test. Every path that cannot answer must
/// end without calling Succeed.
/// </summary>
public class MemberRequirementHandlerTests
{
    private static IConfiguration Config(string? rootIds) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("Admin:DiscordIds", rootIds)])
            .Build();

    /// <summary>Deliberately empty: GetService&lt;RmvDbContext&gt; returns null.</summary>
    private static IServiceProvider NoDatabase() => new ServiceCollection().BuildServiceProvider();

    private static ClaimsPrincipal SignedIn(string? id) =>
        new(new ClaimsIdentity(
            id is null ? [] : [new Claim(ClaimTypes.NameIdentifier, id)],
            "TestAuth"));

    private static async Task<bool> AdminAsync(IConfiguration config, ClaimsPrincipal user)
    {
        var requirement = new AdminRequirement();
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await new AdminAuthorizationHandler(
            NoDatabase(), config, NullLogger<AdminAuthorizationHandler>.Instance)
            .HandleAsync(context);

        return context.HasSucceeded;
    }

    private static async Task<bool> ContributorAsync(IConfiguration config, ClaimsPrincipal user)
    {
        var requirement = new ApprovedMemberRequirement();
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await new ApprovedMemberAuthorizationHandler(
            NoDatabase(), config, NullLogger<ApprovedMemberAuthorizationHandler>.Instance)
            .HandleAsync(context);

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
    public async Task An_empty_root_list_means_nobody_not_everybody()
    {
        var user = SignedIn("111222333444555666");

        Assert.False(await AdminAsync(Config(null), user));
        Assert.False(await ContributorAsync(Config(""), user));
    }
}

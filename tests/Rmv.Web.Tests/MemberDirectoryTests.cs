using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Rmv.Web.Data;

namespace Rmv.Web.Tests;

/// <summary>
/// A signed-in caller must always resolve to a member row. The sign-in hook is
/// not enough on its own: a session outlives a deployment, so a valid cookie can
/// predate the hook or come from a sign-in where it failed.
/// </summary>
[Trait("Category", "Database")]
[Collection(NetworkCollection.Name)]
public class MemberDirectoryTests : IAsyncLifetime
{
    private RmvDbContext _db = null!;
    private MemberDirectory _directory = null!;
    private readonly List<string> _created = [];

    public async Task InitializeAsync()
    {
        var cs = Environment.GetEnvironmentVariable("RMV_TEST_POSTGRES")
                 ?? throw new InvalidOperationException("Set RMV_TEST_POSTGRES to run Database tests.");

        _db = new RmvDbContext(new DbContextOptionsBuilder<RmvDbContext>().UseNpgsql(cs).Options);
        await _db.Database.MigrateAsync();
        _directory = new MemberDirectory(_db, NullLogger<MemberDirectory>.Instance);
    }

    public async Task DisposeAsync()
    {
        _db.Members.RemoveRange(_db.Members.Where(m => _created.Contains(m.DiscordId)));
        await _db.SaveChangesAsync();
        await _db.DisposeAsync();
    }

    private ClaimsPrincipal SignedIn(string discordId, string name = "Someone")
    {
        _created.Add(discordId);
        return new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, discordId),
                new Claim(ClaimTypes.Name, name),
            ],
            "TestAuth"));
    }

    [Fact]
    public async Task Creates_the_row_when_it_is_missing()
    {
        // The reported failure: a valid session with no member row.
        var id = $"t{Guid.NewGuid():N}"[..20];

        var member = await _directory.EnsureAsync(SignedIn(id, "NetworkGnome"), default);

        Assert.NotNull(member);
        Assert.Equal(id, member.DiscordId);
        Assert.Equal("NetworkGnome", member.DisplayName);
        // Pending, not Approved: backfilling a row must not grant anything.
        Assert.Equal(MemberStatus.Pending, member.Status);
        Assert.False(member.IsAdmin);
    }

    [Fact]
    public async Task Returns_the_existing_row_without_duplicating_it()
    {
        var id = $"t{Guid.NewGuid():N}"[..20];
        var principal = SignedIn(id);

        var first = await _directory.EnsureAsync(principal, default);
        var second = await _directory.EnsureAsync(principal, default);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await _db.Members.CountAsync(m => m.DiscordId == id));
    }

    [Fact]
    public async Task Does_not_downgrade_an_approved_member()
    {
        var id = $"t{Guid.NewGuid():N}"[..20];
        var principal = SignedIn(id);

        var member = await _directory.EnsureAsync(principal, default);
        member!.Status = MemberStatus.Approved;
        member.IsAdmin = true;
        await _db.SaveChangesAsync();

        var again = await _directory.EnsureAsync(principal, default);

        Assert.Equal(MemberStatus.Approved, again!.Status);
        Assert.True(again.IsAdmin);
    }

    [Fact]
    public async Task An_alias_takes_over_from_the_discord_name()
    {
        // The site names people by Handle, never by whatever Discord returned.
        var id = $"t{Guid.NewGuid():N}"[..20];
        var principal = SignedIn(id, "networkgnome_x9");
        var me = new CurrentMember(_directory);

        Assert.Equal("networkgnome_x9", await me.HandleAsync(principal));

        var member = await _directory.EnsureAsync(principal, default);
        member!.Alias = "NetworkGnome";
        await _db.SaveChangesAsync();

        // A fresh CurrentMember, because it caches for the life of one request.
        Assert.Equal("NetworkGnome", await new CurrentMember(_directory).HandleAsync(principal));
    }

    [Fact]
    public async Task The_masthead_initials_follow_the_alias()
    {
        // The reported bug, through the exact path the masthead uses: the diamond
        // showed NE from the Discord name while the name beside it said Property.
        var id = $"t{Guid.NewGuid():N}"[..20];
        var principal = SignedIn(id, "networkgnome_x9");

        Assert.Equal("NE", await new CurrentMember(_directory).InitialsAsync(principal));

        var member = await _directory.EnsureAsync(principal, default);
        member!.Alias = "Property";
        await _db.SaveChangesAsync();

        var me = new CurrentMember(_directory);
        Assert.Equal("Property", await me.HandleAsync(principal));
        // The two must agree, which is the whole point of deriving one from the
        // other rather than reading the claim for one and the row for the other.
        Assert.Equal("PR", await me.InitialsAsync(principal));
    }

    [Fact]
    public async Task A_blank_alias_falls_back_to_the_discord_name()
    {
        var id = $"t{Guid.NewGuid():N}"[..20];
        var principal = SignedIn(id, "someone_1234");

        var member = await _directory.EnsureAsync(principal, default);
        member!.Alias = "   ";   // The alias form posts whitespace if you clear it.
        await _db.SaveChangesAsync();

        Assert.Equal("someone_1234", await new CurrentMember(_directory).HandleAsync(principal));
    }

    [Fact]
    public async Task An_anonymous_caller_gets_no_member_and_no_lookup()
    {
        var me = new CurrentMember(_directory);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.Null(await me.GetAsync(anonymous));
    }

    [Fact]
    public async Task A_principal_with_no_discord_id_resolves_to_nothing()
    {
        var anonymousish = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Name, "No id")], "TestAuth"));

        Assert.Null(await _directory.EnsureAsync(anonymousish, default));
    }
}

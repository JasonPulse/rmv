using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
        _directory = Directory(rootIds: null);
    }

    /// <summary>
    /// A fresh CurrentMember over the same directory. Fresh matters: it caches for
    /// the life of one request, which is what makes the site give one answer.
    /// </summary>
    private CurrentMember Me() =>
        new(new ConfigurationBuilder().Build(), NullLogger<CurrentMember>.Instance, _directory);

    /// <summary>A directory whose configuration names the given root admin ids.</summary>
    private MemberDirectory Directory(string? rootIds) =>
        new(_db,
            new ConfigurationBuilder()
                .AddInMemoryCollection([new KeyValuePair<string, string?>("Admin:DiscordIds", rootIds)])
                .Build(),
            NullLogger<MemberDirectory>.Instance);

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
        var me = Me();

        Assert.Equal("networkgnome_x9", await me.HandleAsync(principal));

        var member = await _directory.EnsureAsync(principal, default);
        member!.Alias = "NetworkGnome";
        await _db.SaveChangesAsync();

        // A fresh CurrentMember, because it caches for the life of one request.
        Assert.Equal("NetworkGnome", await Me().HandleAsync(principal));
    }

    [Fact]
    public async Task The_masthead_initials_follow_the_alias()
    {
        // The reported bug, through the exact path the masthead uses: the diamond
        // showed NE from the Discord name while the name beside it said Property.
        var id = $"t{Guid.NewGuid():N}"[..20];
        var principal = SignedIn(id, "networkgnome_x9");

        Assert.Equal("NE", await Me().InitialsAsync(principal));

        var member = await _directory.EnsureAsync(principal, default);
        member!.Alias = "Property";
        await _db.SaveChangesAsync();

        var me = Me();
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

        Assert.Equal("someone_1234", await Me().HandleAsync(principal));
    }

    [Fact]
    public async Task An_anonymous_caller_gets_no_member_and_no_lookup()
    {
        var me = Me();
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

    // --- the sign-in hook ----------------------------------------------------
    //
    // Program.cs used to upsert the row itself, with different rules from this
    // class: IsAdmin false, Status left at its default. So a root admin's first
    // ever row said Pending while configuration said they ran the site, which is
    // the row the admin table printed "PENDING" next to "ROOT". One writer now, and
    // these are the cases that used to differ between the two.

    /// <summary>The identity, registered for cleanup, as the OAuth hook builds it.</summary>
    private DiscordIdentity Identity(string id, string name = "Someone", string? avatar = null)
    {
        _created.Add(id);
        return new DiscordIdentity(id, name, avatar);
    }

    [Fact]
    public async Task A_sign_in_records_the_member()
    {
        var id = $"t{Guid.NewGuid():N}"[..20];

        var member = await _directory.RecordSignInAsync(Identity(id, "NetworkGnome", "abc"), default);

        Assert.NotNull(member);
        Assert.Equal("NetworkGnome", member.DisplayName);
        Assert.Equal("abc", member.AvatarHash);
        Assert.Equal(MemberStatus.Pending, member.Status);
        Assert.False(member.IsAdmin);
    }

    [Fact]
    public async Task A_root_admins_first_sign_in_is_already_approved_and_admin()
    {
        // This is the one that was wrong. The hook created the row, so a root admin
        // was Pending until some later page load happened to reconcile it.
        var id = $"t{Guid.NewGuid():N}"[..20];

        var member = await Directory(id).RecordSignInAsync(Identity(id, "Root"), default);

        Assert.NotNull(member);
        Assert.Equal(MemberStatus.Approved, member.Status);
        Assert.True(member.IsAdmin);
        Assert.Equal("Admin:DiscordIds", member.ApprovedBy);

        // Written, not just returned.
        var stored = await _db.Members.AsNoTracking().FirstAsync(m => m.DiscordId == id);
        Assert.Equal(MemberStatus.Approved, stored.Status);
        Assert.True(stored.IsAdmin);
    }

    [Fact]
    public async Task A_later_sign_in_refreshes_what_discord_says()
    {
        var id = $"t{Guid.NewGuid():N}"[..20];

        var first = await _directory.RecordSignInAsync(Identity(id, "old_name", "oldhash"), default);
        var firstSeen = first!.FirstSeenAt;

        var again = await _directory.RecordSignInAsync(Identity(id, "new_name", "newhash"), default);

        Assert.Equal("new_name", again!.DisplayName);
        Assert.Equal("newhash", again.AvatarHash);
        // First seen is when they first signed in, and stays that way.
        Assert.Equal(firstSeen, again.FirstSeenAt);
        Assert.Equal(1, await _db.Members.CountAsync(m => m.DiscordId == id));
    }

    [Fact]
    public async Task An_alias_survives_a_sign_in()
    {
        // The name a member chose here is theirs, not Discord's. A sign-in refreshes
        // the Discord name beside it and must not touch the alias.
        var id = $"t{Guid.NewGuid():N}"[..20];

        var member = await _directory.RecordSignInAsync(Identity(id, "discord_handle"), default);
        member!.Alias = "Property";
        await _db.SaveChangesAsync();

        var again = await _directory.RecordSignInAsync(Identity(id, "discord_handle_2"), default);

        Assert.Equal("Property", again!.Alias);
        Assert.Equal("Property", again.Handle);
    }

    [Fact]
    public async Task An_ordinary_page_view_does_not_rewrite_the_name()
    {
        // EnsureAsync runs on every request. Copying the claims back over the row
        // each time would be a write per page view, and the claims came off this row
        // at sign-in anyway.
        var id = $"t{Guid.NewGuid():N}"[..20];

        await _directory.RecordSignInAsync(Identity(id, "at_sign_in"), default);

        var seen = await _directory.EnsureAsync(SignedIn(id, "something_else"), default);

        Assert.Equal("at_sign_in", seen!.DisplayName);
    }

    // --- root admins ---------------------------------------------------------

    [Fact]
    public async Task A_root_admin_is_created_approved_and_admin()
    {
        // Their access comes from configuration, so a row saying Pending would be a
        // lie rather than a restriction.
        var id = $"t{Guid.NewGuid():N}"[..20];

        var member = await Directory(id).EnsureAsync(SignedIn(id, "Root"), default);

        Assert.NotNull(member);
        Assert.Equal(MemberStatus.Approved, member.Status);
        Assert.True(member.IsAdmin);
        Assert.Equal("Admin:DiscordIds", member.ApprovedBy);
        Assert.NotNull(member.ApprovedAt);
    }

    [Fact]
    public async Task An_existing_pending_root_admin_is_corrected_on_the_next_access()
    {
        // The reported state: root by configuration, Pending in the table, which
        // meant no upload button on the gallery and no removing anyone else's
        // screenshot, because those read the row while the policies read the config.
        var id = $"t{Guid.NewGuid():N}"[..20];
        var principal = SignedIn(id, "Root");

        var before = await _directory.EnsureAsync(principal, default);
        Assert.Equal(MemberStatus.Pending, before!.Status);
        Assert.False(before.IsAdmin);

        var after = await Directory(id).EnsureAsync(principal, default);

        Assert.Equal(MemberStatus.Approved, after!.Status);
        Assert.True(after.IsAdmin);

        // Written, not just returned.
        var reread = await _db.Members.AsNoTracking().FirstAsync(m => m.DiscordId == id);
        Assert.Equal(MemberStatus.Approved, reread.Status);
        Assert.True(reread.IsAdmin);
    }

    [Fact]
    public async Task A_blocked_root_admin_is_corrected_too()
    {
        // Blocking one is not something this application can do: the policies read
        // the configured ids before the database, so the block was already
        // ineffective and the row was only misleading.
        var id = $"t{Guid.NewGuid():N}"[..20];
        var principal = SignedIn(id, "Root");

        var member = await _directory.EnsureAsync(principal, default);
        member!.Status = MemberStatus.Blocked;
        await _db.SaveChangesAsync();

        var after = await Directory(id).EnsureAsync(principal, default);

        Assert.Equal(MemberStatus.Approved, after!.Status);
        Assert.True(after.IsAdmin);
    }

    [Fact]
    public async Task An_ordinary_member_is_still_pending_and_stays_that_way()
    {
        // The approval gate is the point. Naming somebody else as root must not
        // promote this one.
        var id = $"t{Guid.NewGuid():N}"[..20];
        var principal = SignedIn(id, "Ordinary");

        var member = await Directory("999888777666555444").EnsureAsync(principal, default);

        Assert.Equal(MemberStatus.Pending, member!.Status);
        Assert.False(member.IsAdmin);
    }

    [Fact]
    public async Task A_root_admin_who_was_already_right_is_not_written_again()
    {
        var id = $"t{Guid.NewGuid():N}"[..20];
        var principal = SignedIn(id, "Root");
        var directory = Directory(id);

        var first = await directory.EnsureAsync(principal, default);
        var approvedAt = first!.ApprovedAt;

        var second = await directory.EnsureAsync(principal, default);

        // Same timestamp, so the second access did not rewrite the row.
        Assert.Equal(approvedAt, second!.ApprovedAt);
    }

    [Fact]
    public async Task An_admin_promoted_in_the_table_keeps_their_own_approver()
    {
        // Someone an admin promoted at /admin/members, who then also gets named in
        // configuration. The audit trail of who approved them is not overwritten.
        var id = $"t{Guid.NewGuid():N}"[..20];
        var principal = SignedIn(id, "Promoted");

        var member = await _directory.EnsureAsync(principal, default);
        member!.Status = MemberStatus.Approved;
        member.ApprovedBy = "SomeAdmin";
        member.ApprovedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await _db.SaveChangesAsync();

        var after = await Directory(id).EnsureAsync(principal, default);

        Assert.True(after!.IsAdmin);
        Assert.Equal("SomeAdmin", after.ApprovedBy);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), after.ApprovedAt);
    }
}

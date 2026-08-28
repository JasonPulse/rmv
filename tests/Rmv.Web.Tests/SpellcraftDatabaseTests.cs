using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Rmv.Web.Data;
using Rmv.Web.Tools.Spellcraft;

namespace Rmv.Web.Tests;

/// <summary>
/// What both spellcraft template suites need: a store, somebody else to be
/// scoped against, a member who has signed in but has not been approved, and a
/// teardown that takes the extra rows away.
///
/// Extracted the moment there were two of them, rather than after the copies had
/// drifted, which is the mistake HeraldDatabaseTests records.
///
/// Needs RMV_TEST_POSTGRES, so tagged Database and excluded from CI.
/// </summary>
public abstract class SpellcraftDatabaseTests : HeraldDatabaseTests
{
    protected SpellcraftTemplateStore Store { get; private set; } = null!;

    /// <summary>The tables the site itself ships, so the codes here are real codes.</summary>
    protected static readonly SpellcraftTables Tables = PlaceholderSpellcraftTables.Build();

    /// <summary>Another approved member, so ownership scoping has something to scope against.</summary>
    protected Member Other { get; private set; } = null!;

    /// <summary>Signed in, not approved. The case an [Authorize] alone would let through.</summary>
    protected Member Pending { get; private set; } = null!;

    protected override void ConfigureHerald(FakeHeraldAdapter herald)
    {
        // Nothing here fetches a character. The fixture wants a herald, so it gets
        // one that knows nobody.
    }

    protected override async Task SeedAsync()
    {
        Store = new SpellcraftTemplateStore(Db, NullLogger<SpellcraftTemplateStore>.Instance);

        Other = NewMember(MemberStatus.Approved, "Someone Else");
        Pending = NewMember(MemberStatus.Pending, "Not Approved Yet");

        Db.Members.AddRange(Other, Pending);
        await Db.SaveChangesAsync();
    }

    private static Member NewMember(MemberStatus status, string name) => new()
    {
        DiscordId = $"{Guid.NewGuid():N}"[..18],
        DisplayName = name,
        Status = status,
        FirstSeenAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// The base class disposes its context before this runs, so the extra members
    /// go through a fresh one. Their templates follow on the cascade.
    /// </summary>
    protected override async ValueTask DisposeExtraAsync()
    {
        await using var db = new RmvDbContext(new DbContextOptionsBuilder<RmvDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);

        await db.Members
            .Where(m => m.Id == Other.Id || m.Id == Pending.Id)
            .ExecuteDeleteAsync();
    }

    /// <summary>A design in codes the shipped tables actually contain.</summary>
    protected static SpellcraftDesign Design(string slot = "chest", params string[] gems) =>
        new("alb", slot, 51, gems);

    protected Task<int> CountAsync(int memberId) =>
        Db.SpellcraftTemplates.CountAsync(t => t.MemberId == memberId);

    /// <summary>Fills a member's allowance, and asserts it actually filled.</summary>
    protected async Task FillToCapAsync(int memberId)
    {
        for (var i = 1; i <= SpellcraftTemplate.MaxPerMember; i++)
        {
            var outcome = await Store.SaveAsync(memberId, $"Template {i}", Design(), null, default);
            Assert.True(outcome.Ok, outcome.Error);
        }

        Assert.Equal(SpellcraftTemplate.MaxPerMember, await CountAsync(memberId));
    }
}

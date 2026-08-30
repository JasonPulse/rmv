using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

namespace Rmv.Web.Tests;

/// <summary>
/// Who appears publicly, as one rule.
///
/// It was seven copies of "status is not Blocked", two of them inverted, spread
/// across the history page, the leaderboards, the gallery, both image endpoints and
/// the roster page. They agreed, so nothing was visibly broken. The hazard is the
/// next status: added to the enum, applied in three of the seven, and a member the
/// site means to hide keeps appearing in the other four.
/// </summary>
public class RosterVisibilityRuleTests
{
    private static Member M(MemberStatus status) =>
        new() { DiscordId = "1", DisplayName = "Someone", Status = status };

    [Theory]
    [InlineData(MemberStatus.Approved, true)]
    // Pending appears: they cannot add anything, so they own nothing to hide, and
    // hiding them would blank a character for as long as an approval takes.
    [InlineData(MemberStatus.Pending, true)]
    [InlineData(MemberStatus.Blocked, false)]
    public void Each_status_either_appears_or_does_not(MemberStatus status, bool shows)
    {
        Assert.Equal(shows, RosterVisibility.Shows(M(status)));
    }

    [Fact]
    public void Nobody_is_not_shown()
    {
        // An orphaned row belongs to nobody, so it is nobody's to show.
        Assert.False(RosterVisibility.Shows(null));
    }

    [Fact]
    public void The_rule_covers_every_status_that_exists()
    {
        // If a status is added and left out of Visible it is hidden, which is the
        // safe default. This test is here to make that a decision rather than an
        // oversight: it fails until someone says which side the new one is on.
        var accounted = Enum.GetValues<MemberStatus>()
            .Where(s => RosterVisibility.Visible.Contains(s) || s == MemberStatus.Blocked)
            .ToArray();

        Assert.Equal(Enum.GetValues<MemberStatus>().Length, accounted.Length);
    }
}

/// <summary>
/// The rule applied through the real queries: a blocked member's content leaves
/// every public listing and both image endpoints together, because they all call
/// the same two extensions.
/// </summary>
public class RosterVisibilityQueryTests : HeraldDatabaseTests
{
    private Member _blocked = null!;

    protected override void ConfigureHerald(FakeHeraldAdapter herald) { }

    protected override async Task SeedAsync()
    {
        _blocked = await NewMemberAsync("Blocked One", MemberStatus.Blocked);

        Db.Characters.AddRange(
            Character(Member.Id, "Visible"),
            Character(_blocked.Id, "Hidden"));

        Db.Screenshots.AddRange(
            NewScreenshot(Member.Id, "visible shot"),
            NewScreenshot(_blocked.Id, "hidden shot"));

        await Db.SaveChangesAsync();
    }

    /// <summary>The names the public roster would show for the herald game.</summary>
    private Task<List<string>> OnRosterNamesAsync() =>
        Db.Characters
            .Where(c => c.GamePresenceId == HeraldGameId)
            .OnRoster()
            .Select(c => c.Name)
            .ToListAsync();

    private Character Character(int memberId, string name) => new()
    {
        MemberId = memberId,
        GamePresenceId = HeraldGameId,
        Name = name,
        Source = CharacterSource.Herald,
        Score = 100,
        Level = 50,
        AddedAt = DateTimeOffset.UtcNow,
        PortraitVersion = "abc123",
    };

    [Fact]
    public async Task Characters_of_a_blocked_member_are_not_on_the_roster()
    {
        var names = await OnRosterNamesAsync();

        Assert.Contains("Visible", names);
        Assert.DoesNotContain("Hidden", names);
    }

    [Fact]
    public async Task Screenshots_of_a_blocked_member_are_not_on_the_roster()
    {
        var captions = await Db.Screenshots
            .Where(s => s.MemberId == Member.Id || s.MemberId == _blocked.Id)
            .OnRoster()
            .Select(s => s.Caption)
            .ToListAsync();

        Assert.Contains("visible shot", captions);
        Assert.DoesNotContain("hidden shot", captions);
    }

    [Fact]
    public async Task A_pending_member_is_still_on_the_roster()
    {
        // The half of the rule that is easy to lose when someone writes it as
        // "approved only" in one place.
        var pending = await NewMemberAsync("Waiting", MemberStatus.Pending);
        Db.Characters.Add(Character(pending.Id, "Waiting One"));
        await Db.SaveChangesAsync();

        var names = await OnRosterNamesAsync();

        Assert.Contains("Waiting One", names);
    }

    [Fact]
    public async Task The_herald_filter_takes_only_fetched_characters_on_a_herald_game()
    {
        var manual = new Character
        {
            MemberId = Member.Id,
            GamePresenceId = NoHeraldGameId,
            Name = "Typed In",
            Source = CharacterSource.Manual,
            Level = 20,
            AddedAt = DateTimeOffset.UtcNow,
        };

        // A herald-sourced row on a game with no adapter, which is what a game
        // losing its herald leaves behind.
        var orphaned = new Character
        {
            MemberId = Member.Id,
            GamePresenceId = NoHeraldGameId,
            Name = "Orphaned",
            Source = CharacterSource.Herald,
            Level = 20,
            AddedAt = DateTimeOffset.UtcNow,
        };

        Db.Characters.AddRange(manual, orphaned);
        await Db.SaveChangesAsync();

        var names = await Db.Characters
            .Where(c => c.MemberId == Member.Id)
            .FromHerald()
            .Select(c => c.Name)
            .ToListAsync();

        Assert.Contains("Visible", names);
        Assert.DoesNotContain("Typed In", names);
        Assert.DoesNotContain("Orphaned", names);
    }
}

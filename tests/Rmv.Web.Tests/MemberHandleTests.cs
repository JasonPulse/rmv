using Rmv.Web.Data;

namespace Rmv.Web.Tests;

public class MemberHandleTests
{
    private static Member With(string discordName, string? alias) =>
        new() { DisplayName = discordName, Alias = alias };

    [Fact]
    public void The_alias_wins_when_set()
    {
        // The reason this exists: Discord names are whoever got there first.
        Assert.Equal("NetworkGnome", With("networkgnome_x9", "NetworkGnome").Handle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Falls_back_to_the_discord_name(string? alias)
    {
        Assert.Equal("xxdragonslayerxx", With("xxdragonslayerxx", alias).Handle);
    }

    [Fact]
    public void Handle_is_never_the_discord_id()
    {
        // Roster pages are public, so the id must not leak through the name.
        var m = new Member
        {
            DiscordId = "111222333444555666",
            DisplayName = "someone",
            Alias = "Thorgrim",
        };

        Assert.Equal("Thorgrim", m.Handle);
        Assert.DoesNotContain(m.DiscordId, m.Handle);
    }

    [Fact]
    public void Initials_follow_the_alias_not_the_discord_name()
    {
        // The reported bug: the masthead diamond showed NE from "networkgnome_x9"
        // while the name beside it and the roster both said Property. Three copies
        // of this logic, one of them reading the Discord claim.
        var m = With("networkgnome_x9", "Property");

        Assert.Equal("PR", m.Initials);
        Assert.Equal(Member.InitialsOf(m.Handle), m.Initials);
    }

    [Fact]
    public void Initials_fall_back_with_the_handle()
    {
        Assert.Equal("NE", With("networkgnome_x9", null).Initials);
    }

    [Theory]
    [InlineData("Property", "PR")]
    [InlineData("x", "X")]
    [InlineData("_-_", "?")]          // Punctuation only: the frame still needs something.
    [InlineData("", "?")]
    [InlineData(null, "?")]
    [InlineData("42nd Legion", "42")] // Digits count, so a numeric name is not "?".
    [InlineData("  ada  ", "AD")]
    public void Two_letters_or_a_question_mark(string? name, string expected)
    {
        Assert.Equal(expected, Member.InitialsOf(name));
    }
}

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
}

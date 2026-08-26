using Microsoft.Extensions.Configuration;
using Rmv.Web.Data;

namespace Rmv.Web.Tests;

public class AdminPolicyTests
{
    private static IConfiguration Config(string? ids) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("Admin:DiscordIds", ids)])
            .Build();

    [Theory]
    [InlineData("111,222", new[] { "111", "222" })]
    [InlineData("111 222", new[] { "111", "222" })]
    [InlineData("111;222", new[] { "111", "222" })]
    [InlineData("111,\n222\t", new[] { "111", "222" })]
    [InlineData(" 111 , 222 ", new[] { "111", "222" })]
    [InlineData("111,111,222", new[] { "111", "222" })]
    public void Parses_every_separator_and_deduplicates(string input, string[] expected)
    {
        Assert.Equal(expected, AdminPolicy.Parse(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,, ;")]
    public void An_unset_list_yields_no_admins(string? input)
    {
        // Fails closed. An empty allowlist must not mean everybody.
        Assert.Empty(AdminPolicy.Parse(input));
    }

    [Fact]
    public void Root_admin_matches_only_an_exact_id()
    {
        var config = Config("111222333444555666");

        Assert.True(AdminPolicy.IsRootAdmin(config, "111222333444555666"));
        // Substrings and prefixes must not match, or a shorter id would inherit
        // someone else's access.
        Assert.False(AdminPolicy.IsRootAdmin(config, "111222333444555"));
        Assert.False(AdminPolicy.IsRootAdmin(config, "111222333444555666777"));
        Assert.False(AdminPolicy.IsRootAdmin(config, "999888777666555444"));
    }

    [Fact]
    public void Nobody_is_root_when_the_list_is_empty()
    {
        Assert.False(AdminPolicy.IsRootAdmin(Config(null), "111222333444555666"));
        Assert.False(AdminPolicy.IsRootAdmin(Config(""), "111222333444555666"));
    }

    [Fact]
    public void A_null_id_is_never_root()
    {
        Assert.False(AdminPolicy.IsRootAdmin(Config("111"), null));
    }
}

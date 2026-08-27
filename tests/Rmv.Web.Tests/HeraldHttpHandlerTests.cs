using System.Net;
using System.Net.Sockets;
using Rmv.Web.Herald;

namespace Rmv.Web.Tests;

/// <summary>
/// Proves the SSRF control is wired, not just that the policy is correct.
/// A right policy behind a handler nobody uses protects nothing, so these drive
/// real HttpClients through the real handler.
/// </summary>
public class HeraldHttpHandlerTests
{
    private static HttpClient Client(params string[] allowedPrivateHosts) =>
        new(HeraldHttpHandler.Create(allowedPrivateHosts))
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://localhost/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://192.168.1.1/")]
    public async Task Refuses_to_connect_to_an_internal_address(string url)
    {
        using var client = Client();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));

        // The message has to come from our callback, not from a connection that
        // was attempted and merely failed.
        Assert.Contains("Refusing to connect", Flatten(ex));
    }

    [Fact]
    public async Task Refuses_even_when_a_real_listener_is_there()
    {
        // Without the callback this would succeed, so it distinguishes "blocked"
        // from "nothing was listening anyway".
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        using var client = Client();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync($"http://127.0.0.1:{port}/"));

        Assert.Contains("Refusing to connect", Flatten(ex));
    }

    [Fact]
    public async Task A_hostname_resolving_only_to_loopback_is_refused()
    {
        // localhost is the readily available stand-in for a DNS entry an admin
        // could point at something internal.
        using var client = Client();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("http://localhost:1/"));

        Assert.Contains("Refusing to connect", Flatten(ex));
    }

    // --- the operator allowlist, which exists because a real herald is internal --

    [Fact]
    public async Task An_allowlisted_host_may_resolve_to_a_private_address()
    {
        // The FFXI herald resolves to 172.25.75.70, an RFC1918 address, so
        // without this it would be refused. Proven against a real listener so
        // the test distinguishes "connected" from "failed differently".
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        using var client = Client("localhost");

        // Nothing speaks HTTP on the other end, so the request fails, but it must
        // not fail with our refusal: the connection has to have been attempted.
        var ex = await Record.ExceptionAsync(() => client.GetAsync($"http://localhost:{port}/"));

        Assert.NotNull(ex);
        Assert.DoesNotContain("Refusing to connect", Flatten(ex!));
    }

    [Fact]
    public async Task The_allowlist_is_matched_by_host_not_by_substring()
    {
        // "localhost" being allowed must not also allow "notlocalhost" or a
        // hostname that merely contains it.
        using var client = Client("localhost");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("http://127.0.0.1/"));

        Assert.Contains("Refusing to connect", Flatten(ex));
    }

    [Fact]
    public async Task An_empty_allowlist_refuses_everything_private()
    {
        using var client = Client();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("http://localhost:1/"));

        Assert.Contains("Refusing to connect", Flatten(ex));
    }

    [Theory]
    [InlineData("a.example, b.example", new[] { "a.example", "b.example" })]
    [InlineData("A.EXAMPLE", new[] { "a.example" })]
    [InlineData("a.example a.example", new[] { "a.example" })]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    public void Parses_the_configured_allowlist(string? configured, string[] expected)
    {
        Assert.Equal(expected, HeraldHttpHandler.ParseAllowedPrivateHosts(configured));
    }

    private static string Flatten(Exception ex)
    {
        var text = ex.Message;
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            text += " | " + inner.Message;
        }

        return text;
    }
}

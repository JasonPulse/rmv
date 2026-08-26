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
    private static HttpClient Client() => new(HeraldHttpHandler.Create())
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

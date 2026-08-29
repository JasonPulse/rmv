using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Rmv.Web.Herald;

namespace Rmv.Web.Tests;

/// <summary>
/// Whether a server counts as up, and what the state holder does with an answer.
///
/// The check itself goes through the same SSRF-guarded fetcher as everything else,
/// so a stub handler stands in for the server rather than a real one being polled
/// by a test suite.
/// </summary>
public class ServerStatusTests
{
    private sealed class Stub(HttpStatusCode? status, Exception? throws = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public bool BodyRead { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;

            if (throws is not null)
            {
                throw throws;
            }

            // A body that records being read, so "the ping does not download the
            // page" is a property a test can actually see.
            var content = new WatchedContent(() => BodyRead = true);
            return Task.FromResult(new HttpResponseMessage(status!.Value) { Content = content });
        }
    }

    private sealed class WatchedContent(Action onRead) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            onRead();
            return stream.WriteAsync(new byte[] { 1, 2, 3 }).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 3;
            return true;
        }
    }

    private static HeraldFetcher Fetcher(HttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<HeraldFetcher>.Instance);

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Moved)]
    // Any answer means the server is up. A 403 or a 404 on a front page is a
    // configuration question, not an outage, and reporting it as down would put a
    // red light on a server people are happily playing on.
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Anything_short_of_a_server_error_is_up(HttpStatusCode status)
    {
        var (ok, ms, error) = await Fetcher(new Stub(status))
            .PingAsync("https://example.test/", default);

        Assert.True(ok, error);
        Assert.Null(error);
        Assert.True(ms >= 0);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_server_error_is_down(HttpStatusCode status)
    {
        var (ok, _, error) = await Fetcher(new Stub(status))
            .PingAsync("https://example.test/", default);

        Assert.False(ok);
        Assert.Contains(((int)status).ToString(), error);
    }

    [Fact]
    public async Task Not_answering_at_all_is_down_with_the_reason()
    {
        var (ok, _, error) = await Fetcher(new Stub(null, new HttpRequestException("No such host")))
            .PingAsync("https://example.test/", default);

        Assert.False(ok);
        Assert.Contains("No such host", error);
    }

    [Fact]
    public async Task A_ping_does_not_download_the_page()
    {
        // On a timer against someone else's server, so pulling a page down to learn
        // one bit would be both rude and pointless.
        var stub = new Stub(HttpStatusCode.OK);

        await Fetcher(stub).PingAsync("https://example.test/", default);

        Assert.Equal(1, stub.Calls);
        Assert.False(stub.BodyRead);
    }

    [Fact]
    public async Task A_url_that_is_not_absolute_http_is_refused_without_a_request()
    {
        var stub = new Stub(HttpStatusCode.OK);

        var (ok, _, error) = await Fetcher(stub).PingAsync("javascript:alert(1)", default);

        Assert.False(ok);
        Assert.Equal(0, stub.Calls);
        Assert.Contains("absolute", error);
    }

    [Fact]
    public void The_state_starts_empty_so_the_panel_is_absent_rather_than_unknown()
    {
        Assert.Empty(new ServerStatusState().All);
    }

    [Fact]
    public void The_state_hands_back_what_was_last_set()
    {
        var state = new ServerStatusState();
        var now = DateTimeOffset.UtcNow;

        state.Set([new ServerStatus("Blackthorn DAoC", true, 42, now, null)]);
        Assert.Equal("Blackthorn DAoC", Assert.Single(state.All).Game);

        // Replaced wholesale, not merged: a game that stops being active stops
        // being on the wall.
        state.Set([new ServerStatus("Final Fantasy XI", false, 0, now, "timeout")]);
        var only = Assert.Single(state.All);
        Assert.Equal("Final Fantasy XI", only.Game);
        Assert.False(only.Ok);
    }
}

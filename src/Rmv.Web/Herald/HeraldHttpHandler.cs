using System.Net;
using System.Net.Sockets;

namespace Rmv.Web.Herald;

public static class HeraldHttpHandler
{
    /// <summary>
    /// The handler used for every herald fetch.
    ///
    /// The connect callback is the actual SSRF control, not the URL validation.
    /// Checking the URL string is not enough, for two reasons: a perfectly
    /// well-formed hostname can resolve to 10.0.0.5, and a hostname that resolved
    /// somewhere public when an admin saved it can resolve somewhere internal by
    /// the time it is fetched. Vetting at connect time also covers every redirect
    /// hop, because each hop opens its own connection.
    ///
    /// The socket is connected to an address that has already been vetted, rather
    /// than to the hostname, so nothing can re-resolve between the check and the
    /// connection.
    /// </summary>
    public static SocketsHttpHandler Create() => new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 3,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        ConnectCallback = ConnectAsync,
    };

    internal static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;

        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(AddressPolicy.IsAllowed).ToArray();

        if (allowed.Length == 0)
        {
            throw new HttpRequestException(
                $"Refusing to connect to {host}: it resolves only to addresses that are not public.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

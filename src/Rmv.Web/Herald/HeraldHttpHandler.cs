using System.Net;
using System.Net.Sockets;

namespace Rmv.Web.Herald;

public static class HeraldHttpHandler
{
    /// <summary>
    /// Hosts permitted to resolve to a private address, from
    /// Herald:AllowedPrivateHosts.
    ///
    /// This exists because a legitimate herald can be internal. The FFXI one
    /// resolves publicly to 172.25.75.70, which is RFC1918, so the address check
    /// alone would refuse a herald that is supposed to work.
    ///
    /// The important part is *where* the allowlist lives. It is configuration,
    /// set by whoever controls the deployment, and is deliberately not editable
    /// from the admin UI. A web admin can point a game at any public herald; only
    /// an operator can permit an internal address. Otherwise the SSRF guard would
    /// be bypassable by the very people it is meant to constrain.
    /// </summary>
    public static string[] ParseAllowedPrivateHosts(string? configured) =>
        (configured ?? "")
            .Split([',', ' ', ';', '\n', '\r', '\t'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(h => h.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The handler used for every herald fetch.
    ///
    /// The connect callback is the actual SSRF control, not the URL validation.
    /// Checking the URL string is not enough, for two reasons: a well-formed
    /// hostname can resolve to a private address, and a hostname that resolved
    /// somewhere public when an admin saved it can resolve somewhere internal by
    /// the time it is fetched. Vetting at connect time also covers every redirect
    /// hop, because each hop opens its own connection.
    ///
    /// The socket connects to an address that has already been vetted, rather
    /// than to the hostname, so nothing can re-resolve in between.
    /// </summary>
    public static SocketsHttpHandler Create(IEnumerable<string>? allowedPrivateHosts = null)
    {
        var allowed = new HashSet<string>(
            (allowedPrivateHosts ?? []).Select(h => h.ToLowerInvariant()),
            StringComparer.Ordinal);

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            ConnectCallback = (context, ct) => ConnectAsync(context, allowed, ct),
        };
    }

    internal static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, HashSet<string> allowedPrivateHosts, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var trusted = allowedPrivateHosts.Contains(host.ToLowerInvariant());

        var addresses = await Dns.GetHostAddressesAsync(host, ct);

        // A trusted host skips the address check entirely: an operator has taken
        // responsibility for it by name. Everything else must be public.
        var usable = trusted
            ? addresses
            : addresses.Where(AddressPolicy.IsAllowed).ToArray();

        if (usable.Length == 0)
        {
            throw new HttpRequestException(
                $"Refusing to connect to {host}: it resolves only to addresses that are not public, "
                + "and it is not in Herald:AllowedPrivateHosts.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(usable, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

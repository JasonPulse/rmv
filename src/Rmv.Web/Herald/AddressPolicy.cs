using System.Net;
using System.Net.Sockets;

namespace Rmv.Web.Herald;

/// <summary>
/// Decides whether the server is willing to open a connection to an address.
///
/// Heralds are fetched from URLs an admin typed in. That makes the app an
/// attacker-influenced HTTP client, which is the classic SSRF shape: a URL of
/// http://10.0.0.5/ or http://169.254.169.254/ turns the pod into a proxy for
/// whatever is reachable from inside the cluster. Nothing an admin can type is
/// trusted to be external; the address is checked instead.
///
/// Checked at connect time rather than at save time, so a hostname that resolves
/// somewhere harmless when it is saved and somewhere internal when it is fetched
/// gets caught too.
/// </summary>
public static class AddressPolicy
{
    public static bool IsAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // Anything not v4 or v6 has no business here.
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast
            || address.IsIPv6UniqueLocal
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        // A v4 address wrapped in v6 would otherwise dodge every v4 check below.
        if (address.IsIPv4MappedToIPv6)
        {
            return IsAllowed(address.MapToIPv4());
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();

            return b[0] switch
            {
                0 => false,                                  // 0.0.0.0/8, "this network"
                10 => false,                                 // RFC1918
                127 => false,                                // loopback
                169 when b[1] == 254 => false,               // link-local, cloud metadata lives here
                172 when b[1] >= 16 && b[1] <= 31 => false,  // RFC1918
                192 when b[1] == 168 => false,               // RFC1918
                192 when b[1] == 0 && b[2] == 0 => false,    // IETF protocol assignments
                100 when b[1] >= 64 && b[1] <= 127 => false, // RFC6598 carrier NAT
                198 when b[1] == 18 || b[1] == 19 => false,  // benchmarking
                >= 224 => false,                             // multicast and reserved
                _ => true,
            };
        }

        return true;
    }
}

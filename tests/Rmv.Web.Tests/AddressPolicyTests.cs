using System.Net;
using Rmv.Web.Herald;

namespace Rmv.Web.Tests;

public class AddressPolicyTests
{
    [Theory]
    // The addresses that make an admin-typed URL dangerous. A herald fetch that
    // reaches any of these turns the pod into a proxy for the cluster.
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.5")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]   // cloud metadata
    [InlineData("100.64.0.1")]        // carrier NAT, also Tailscale's range
    [InlineData("198.18.0.1")]        // benchmarking
    [InlineData("192.0.0.1")]         // IETF protocol assignments
    [InlineData("224.0.0.1")]         // multicast
    [InlineData("255.255.255.255")]
    [InlineData("::1")]               // v6 loopback
    [InlineData("fe80::1")]           // v6 link-local
    [InlineData("fc00::1")]           // v6 unique local
    [InlineData("fd00::1")]           // v6 unique local
    [InlineData("::")]                // v6 any
    [InlineData("::ffff:10.0.0.5")]   // v4 private wrapped in v6
    [InlineData("::ffff:127.0.0.1")]  // v4 loopback wrapped in v6
    public void Refuses_addresses_that_are_not_public(string ip)
    {
        Assert.False(AddressPolicy.IsAllowed(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("104.21.59.10")]
    [InlineData("172.15.255.255")]    // just below the RFC1918 block
    [InlineData("172.32.0.1")]        // just above it
    [InlineData("192.167.255.255")]   // just below 192.168/16
    [InlineData("192.169.0.1")]       // just above it
    [InlineData("169.253.0.1")]       // just below link-local
    [InlineData("100.63.255.255")]    // just below carrier NAT
    [InlineData("100.128.0.1")]       // just above it
    [InlineData("2606:4700:4700::1111")]
    public void Allows_ordinary_public_addresses(string ip)
    {
        Assert.True(AddressPolicy.IsAllowed(IPAddress.Parse(ip)));
    }

    [Fact]
    public void Boundaries_of_the_private_ranges_are_exact()
    {
        // Off-by-one here either blocks a real herald or opens a hole.
        Assert.False(AddressPolicy.IsAllowed(IPAddress.Parse("172.16.0.0")));
        Assert.False(AddressPolicy.IsAllowed(IPAddress.Parse("172.31.255.255")));
        Assert.True(AddressPolicy.IsAllowed(IPAddress.Parse("172.15.0.0")));
        Assert.True(AddressPolicy.IsAllowed(IPAddress.Parse("172.32.0.0")));
    }

    [Fact]
    public void A_null_address_is_a_programming_error_not_a_pass()
    {
        Assert.Throws<ArgumentNullException>(() => AddressPolicy.IsAllowed(null!));
    }
}

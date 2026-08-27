using Microsoft.Extensions.Logging.Abstractions;
using Rmv.Web.Herald;

namespace Rmv.Web.Tests;

/// <summary>
/// Drives the whole pipeline against the real heralds: handler, address policy,
/// allowlist, fetcher, adapter. Nothing is mocked.
///
/// Marked Network and excluded from CI, because the FFXI herald is internal and
/// a build runner cannot reach it. Run locally with:
///   dotnet test --filter Category=Network
/// </summary>
[Trait("Category", "Network")]
public class HeraldLiveTests
{
    private static HeraldFetcher Fetcher(params string[] allowedPrivateHosts)
    {
        var client = new HttpClient(HeraldHttpHandler.Create(allowedPrivateHosts))
        {
            Timeout = TimeSpan.FromSeconds(20),
            MaxResponseContentBufferSize = HeraldFetcher.MaxBytes,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RMV-herald/1.0 (+https://resultsmayvary.org)");

        return new HeraldFetcher(client, NullLogger<HeraldFetcher>.Instance);
    }

    [Fact]
    public async Task Blackthorn_returns_a_real_character()
    {
        var adapter = new BlackthornHeraldAdapter(Fetcher());

        var result = await adapter.FetchCharacterAsync(
            "https://herald.blackthorn-daoc.com", "Enchantress", CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("Enchantress", result.Character!.Name);
        Assert.Equal(50, result.Character.Level);
        Assert.False(string.IsNullOrWhiteSpace(result.Character.Realm));
    }

    [Fact]
    public async Task Blackthorn_says_so_for_a_name_that_does_not_exist()
    {
        var adapter = new BlackthornHeraldAdapter(Fetcher());

        var result = await adapter.FetchCharacterAsync(
            "https://herald.blackthorn-daoc.com", "Zzqxwvunlikelyname", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("no character", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HeraldXi_returns_a_real_character_when_allowlisted()
    {
        var adapter = new HeraldXiAdapter(Fetcher("heraldxi.network-gnomes.com"));

        var result = await adapter.FetchCharacterAsync(
            "https://heraldxi.network-gnomes.com", "Arwen", CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("Arwen", result.Character!.Name);
        Assert.False(string.IsNullOrWhiteSpace(result.Character.Realm));
    }

    [Fact]
    public async Task HeraldXi_is_refused_when_not_allowlisted()
    {
        // The point of the allowlist. Same host, same adapter, no permission.
        var adapter = new HeraldXiAdapter(Fetcher());

        var result = await adapter.FetchCharacterAsync(
            "https://heraldxi.network-gnomes.com", "Arwen", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("not public", result.Error!, StringComparison.OrdinalIgnoreCase);
    }
}

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
[Collection(NetworkCollection.Name)]
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

    /// <summary>
    /// One character, three requests: the search, the profile, the Class/Job page.
    /// Kept to a single test on purpose. The Lodestone is Square Enix's and this
    /// suite has been rude to someone else's server once already.
    /// </summary>
    [Fact]
    public async Task Lodestone_resolves_a_name_and_reads_the_character()
    {
        var adapter = new LodestoneAdapter(Fetcher());

        var result = await adapter.FetchCharacterAsync(
            "https://na.finalfantasyxiv.com", "Aoii Aeredel", CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        var c = result.Character!;
        Assert.Equal("Aoii Aeredel", c.Name);
        Assert.Contains("Exodus", c.Realm);
        // The job's name only exists as an image on the profile, so a name here
        // proves the Class/Job page was fetched and the icons matched.
        Assert.False(string.IsNullOrWhiteSpace(c.Class));
        Assert.NotNull(c.Level);
        Assert.NotNull(c.Portrait);
        Assert.StartsWith("https://img2.finalfantasyxiv.com/", c.Portrait.Url);
    }

    [Fact]
    public async Task Lodestone_takes_a_pasted_character_address_without_searching()
    {
        var adapter = new LodestoneAdapter(Fetcher());

        var result = await adapter.FetchCharacterAsync(
            "https://na.finalfantasyxiv.com",
            "https://na.finalfantasyxiv.com/lodestone/character/8868232/",
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("Aoii Aeredel", result.Character!.Name);
    }

    /// <summary>
    /// The portrait route on the FFXI herald, which its API does not advertise.
    ///
    /// Network rather than default because the herald is internal: a build runner
    /// cannot reach it, and neither can a visitor's browser, which is the whole
    /// reason the bytes are stored rather than linked.
    /// </summary>
    [Fact]
    public async Task HeraldXi_serves_a_real_portrait_when_allowlisted()
    {
        var fetcher = Fetcher("heraldxi.network-gnomes.com");
        var adapter = new HeraldXiAdapter(fetcher);

        var result = await adapter.FetchCharacterAsync(
            "https://heraldxi.network-gnomes.com", "Arwen", CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        var portrait = result.Character!.Portrait;
        Assert.NotNull(portrait);
        Assert.Contains("/portraits/", portrait.Url);

        var image = await fetcher.GetImageAsync(portrait.Url, CancellationToken.None);

        Assert.True(image.Ok, image.Error);
        Assert.Equal("image/png", image.ContentType);
        Assert.NotNull(image.Bytes);
        // A real PNG, not an error page with a helpful status code.
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, image.Bytes.Take(4).ToArray());
        Assert.True(image.Bytes.Length > 1000, $"suspiciously small: {image.Bytes.Length} bytes");
    }

    [Fact]
    public async Task A_portrait_is_refused_when_the_internal_host_is_not_allowlisted()
    {
        // Same guard as the API itself. A portrait must not be the hole in it.
        var image = await Fetcher().GetImageAsync(
            "https://heraldxi.network-gnomes.com/portraits/3.png", CancellationToken.None);

        Assert.False(image.Ok);
        Assert.Contains("not public", image.Error!, StringComparison.OrdinalIgnoreCase);
    }
}

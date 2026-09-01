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

    /// <summary>
    /// Arwen off the real FFXI herald, with the fetcher that found her, since the
    /// portrait lives on the same allowlisted host.
    /// </summary>
    private static async Task<(HeraldFetcher Fetcher, HeraldCharacter Character)> ArwenAsync()
    {
        var fetcher = Fetcher("heraldxi.network-gnomes.com");

        var result = await new HeraldXiAdapter(fetcher).FetchCharacterAsync(
            "https://heraldxi.network-gnomes.com", "Arwen", CancellationToken.None);

        Assert.True(result.Ok, result.Error);

        return (fetcher, result.Character!);
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
    public async Task The_armory_returns_a_real_character()
    {
        // The character whose page prompted this adapter. Nothing is mocked: the
        // real page is fetched and the JSON it carries is parsed.
        var adapter = new WowArmoryAdapter(Fetcher());

        var result = await adapter.FetchCharacterAsync(
            adapter.DefaultBaseUrl, "Syfr-Quel'Thalas", CancellationToken.None);

        Assert.True(result.Ok, result.Error);

        var c = result.Character!;
        Assert.Equal("Syfr", c.Name);
        Assert.Equal("Quel'Thalas (Alliance)", c.Realm);
        Assert.Contains("Death Knight", c.Class!);
        Assert.True(c.Level is >= 1 and <= 90, $"level {c.Level}");
        Assert.NotNull(c.Portrait);
    }

    [Fact]
    public async Task The_armory_pasted_as_an_address_finds_the_same_character()
    {
        var adapter = new WowArmoryAdapter(Fetcher());

        var result = await adapter.FetchCharacterAsync(
            adapter.DefaultBaseUrl,
            "https://worldofwarcraft.blizzard.com/en-us/worldsoul/us/armory/character/quelthalas/syfr",
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("Syfr", result.Character!.Name);
    }

    [Fact]
    public async Task The_armory_says_why_it_might_not_have_a_character()
    {
        // The live shape that matters: the Armory answers 500 rather than 404 for a
        // character it will not show, and a lapsed subscription looks identical to a
        // misspelling. Both explanations have to reach the member.
        var adapter = new WowArmoryAdapter(Fetcher());

        var result = await adapter.FetchCharacterAsync(
            adapter.DefaultBaseUrl, "Zzqxwvunlikelyname-Quel'Thalas", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("no character", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("subscription", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_armory_portrait_is_fetchable()
    {
        // The render is on a different host from the Armory itself, so this is the
        // check that the portrait pipeline reaches it at all.
        var fetcher = Fetcher();
        var adapter = new WowArmoryAdapter(fetcher);

        var result = await adapter.FetchCharacterAsync(
            adapter.DefaultBaseUrl, "Syfr-Quel'Thalas", CancellationToken.None);

        Assert.True(result.Ok, result.Error);

        var image = await fetcher.GetImageAsync(result.Character!.Portrait!.Url, CancellationToken.None);

        Assert.True(image.Ok, image.Error);
        Assert.StartsWith("image/", image.ContentType!);
        Assert.True(image.Bytes!.Length > 1000, $"{image.Bytes.Length} bytes");
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
    public async Task HeraldXi_links_to_a_page_a_person_can_read()
    {
        // The reported bug, checked against the real herald: the link on a card has
        // to be the player page, and that page has to exist.
        var url = (await ArwenAsync()).Character.Url!;
        Assert.EndsWith("/player/Arwen", url);
        Assert.DoesNotContain("/api/", url);

        // And it answers, rather than being a route I assumed.
        var page = await Fetcher("heraldxi.network-gnomes.com").GetAsync(url, CancellationToken.None);
        Assert.True(page.Ok, page.Error);
        Assert.Contains("Arwen", page.Body!);
    }

    [Fact]
    public async Task HeraldXi_serves_a_portrait_we_can_digest()
    {
        // The picture is its own version, so what matters live is that the route
        // answers with real image bytes. Nothing the herald says about the
        // appearance is read; on 2026-08-30 it served two different renders of one
        // character under one hash while calling the response immutable.
        var (fetcher, character) = await ArwenAsync();

        Assert.NotNull(character.Portrait);

        var image = await fetcher.GetImageAsync(character.Portrait.Url, CancellationToken.None);

        Assert.True(image.Ok, image.Error);
        Assert.StartsWith("image/", image.ContentType!);
        Assert.Equal(16, CharacterService.VersionOf(image.Bytes!).Length);
    }

    [Fact]
    public async Task Each_live_herald_fills_in_the_stats_it_declares()
    {
        // A fixture can be out of date about a payload's shape, and a stat the editor
        // offers that no live herald fills is a token that draws nothing forever.
        // Every declared key does not have to appear for one character, but something
        // must.
        var blackthorn = new BlackthornHeraldAdapter(Fetcher());
        var daoc = await blackthorn.FetchCharacterAsync(
            "https://herald.blackthorn-daoc.com", "Enchantress", CancellationToken.None);

        Assert.True(daoc.Ok, daoc.Error);
        Assert.NotNull(daoc.Character!.Stats);
        Assert.NotEmpty(daoc.Character.Stats);

        // The one the 2001 generator had, live.
        Assert.True(daoc.Character.Stats.ContainsKey("LastWeek"),
            $"no LastWeek in [{string.Join(", ", daoc.Character.Stats.Keys)}]");

        var xi = await ArwenAsync();
        Assert.NotNull(xi.Character.Stats);

        // Arwen is a level 1 test character, so most counters are zero and absent.
        // The zone and the nation are not.
        Assert.True(xi.Character.Stats.ContainsKey("Zone") || xi.Character.Stats.ContainsKey("Nation"),
            $"nothing in [{string.Join(", ", xi.Character.Stats.Keys)}]");

        var armory = new WowArmoryAdapter(Fetcher());
        var wow = await armory.FetchCharacterAsync(
            armory.DefaultBaseUrl, "Syfr-Quel'Thalas", CancellationToken.None);

        Assert.True(wow.Ok, wow.Error);
        Assert.NotNull(wow.Character!.Stats);
        Assert.Equal("146", wow.Character.Stats["ItemLevel"]);
        Assert.Equal("Alliance", wow.Character.Stats["Faction"]);
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

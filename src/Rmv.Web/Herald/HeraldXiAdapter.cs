using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rmv.Web.Herald;

/// <summary>
/// The FFXI private server herald. A JSON API rather than a page to scrape, so
/// there is no markup to break: the fields are declared server-side separately
/// from its internal structs, and arrays are never null.
///
/// The host is internal. It resolves publicly to an RFC1918 address, so it only
/// works when its hostname is listed in Herald:AllowedPrivateHosts. Without that
/// the fetch is refused, by design.
/// </summary>
public sealed class HeraldXiAdapter(HeraldFetcher fetcher) : IHeraldAdapter
{
    public string Key => "heraldxi";

    public string DisplayName => "HeraldXI (FFXI)";

    public string BaseUrlHint => "https://heraldxi.network-gnomes.com";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public async Task<HeraldResult> FetchCharacterAsync(
        string baseUrl, string characterName, CancellationToken ct)
    {
        if (!TryBuildUrl(baseUrl, characterName, out var url))
        {
            return HeraldResult.Fail("That herald address or character name does not look right.");
        }

        var fetched = await fetcher.GetAsync(url, ct);
        if (!fetched.Ok)
        {
            // A 404 means the name is wrong, which is the common case and worth
            // saying plainly rather than reporting a status code.
            return fetched.NotFound
                ? HeraldResult.Fail($"The herald has no character called \"{characterName}\".")
                : HeraldResult.Fail(fetched.Error ?? "Could not reach the herald.");
        }

        try
        {
            var dto = JsonSerializer.Deserialize<XiCharacter>(fetched.Body!, Json);
            if (dto is null || string.IsNullOrWhiteSpace(dto.Name))
            {
                return HeraldResult.Fail($"The herald has no character called \"{characterName}\".");
            }

            return HeraldResult.Found(Map(dto, url));
        }
        catch (JsonException ex)
        {
            return HeraldResult.Fail($"The herald returned something unexpected: {ex.Message}");
        }
    }

    public static bool TryBuildUrl(string baseUrl, string characterName, out string url)
    {
        url = "";

        if (!Data.ExternalUrl.TryParse(baseUrl, out var root)
            || !IsPlausibleCharacterName(characterName))
        {
            return false;
        }

        url = $"{root.TrimEnd('/')}/api/v1/characters/{Uri.EscapeDataString(characterName)}";
        return true;
    }

    /// <summary>FFXI names are one alphabetic word, capped at 15 in the client.</summary>
    public static bool IsPlausibleCharacterName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 16
        && name.All(char.IsAsciiLetter);

    public static HeraldCharacter Map(XiCharacter dto, string url) => new()
    {
        Name = dto.Name,
        // FFXI has no guild. Nation is the nearest equivalent and is what a
        // signature would show.
        Guild = null,
        Realm = Blank(dto.Nation),
        // Job and level as the game writes them: "MNK 1" or "MNK 75 / WHM 37".
        Class = FormatJob(dto),
        Race = Blank(dto.Race),
        Level = dto.MainJobLevel > 0 ? dto.MainJobLevel : null,
        // No realm points in FFXI; total job levels is the comparable measure of
        // progress and is what the herald's own leaderboards rank on.
        RealmPoints = dto.TotalJobLevels > 0 ? dto.TotalJobLevels : null,
        Kills = dto.Kills > 0 ? dto.Kills : null,
        Deaths = dto.Deaths > 0 ? dto.Deaths : null,
        RealmRank = Blank(dto.Title),
        LastOnline = dto.Online
            ? "Online now"
            : dto.LastLogout is { } t ? t.ToString("yyyy-MM-dd") : null,
        Url = url,
    };

    private static string? FormatJob(XiCharacter dto)
    {
        if (string.IsNullOrWhiteSpace(dto.MainJob))
        {
            return null;
        }

        var main = $"{dto.MainJob} {dto.MainJobLevel}";

        // "---" is how the herald says "no subjob", so it must not be rendered.
        return string.IsNullOrWhiteSpace(dto.SubJob) || dto.SubJob is "---" || dto.SubJobLevel <= 0
            ? main
            : $"{main} / {dto.SubJob} {dto.SubJobLevel}";
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// Only the fields this site uses. The API returns a great deal more
    /// (missions, jobs, skills, crafts, history); ignoring them here means it can
    /// grow without touching this.
    /// </summary>
    public sealed class XiCharacter
    {
        public string Name { get; set; } = "";
        public string? Nation { get; set; }
        public string? Race { get; set; }
        public string? Title { get; set; }

        [JsonPropertyName("main_job")] public string? MainJob { get; set; }
        [JsonPropertyName("main_job_level")] public int MainJobLevel { get; set; }
        [JsonPropertyName("sub_job")] public string? SubJob { get; set; }
        [JsonPropertyName("sub_job_level")] public int SubJobLevel { get; set; }
        [JsonPropertyName("total_job_levels")] public int TotalJobLevels { get; set; }

        public int Kills { get; set; }
        public int Deaths { get; set; }
        public bool Online { get; set; }

        [JsonPropertyName("last_logout")] public DateTimeOffset? LastLogout { get; set; }
    }
}

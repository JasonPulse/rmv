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

    public string DefaultBaseUrl => "https://heraldxi.network-gnomes.com";

    /// <summary>
    /// Total job levels. FFXI has no realm points, and this is the measure of
    /// progress the herald's own leaderboards use.
    /// </summary>
    public LeaderboardMetric Metric => new(RankBy.Score, "Total job levels");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public async Task<HeraldResult> FetchCharacterAsync(
        string baseUrl, string characterName, CancellationToken ct)
    {
        if (!TryApiUrl(baseUrl, characterName, out var api))
        {
            return HeraldResult.Fail("That herald address or character name does not look right.");
        }

        var (body, failure) = await fetcher.GetForCharacterAsync(api, characterName, ct);
        if (body is null)
        {
            return failure!;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<XiCharacter>(body, Json);
            if (dto is null || string.IsNullOrWhiteSpace(dto.Name))
            {
                return HeraldResult.Fail($"The herald has no character called \"{characterName}\".");
            }

            // The herald's own page, not the API record we just read: the name comes
            // from the herald's echo of it, so the link is right even when the
            // member typed it in the wrong case.
            return HeraldResult.Found(Map(dto, PlayerUrl(baseUrl, dto.Name), baseUrl));
        }
        catch (JsonException ex)
        {
            return HeraldResult.Fail($"The herald returned something unexpected: {ex.Message}");
        }
    }

    /// <summary>
    /// The API record for a character, which is what this adapter fetches.
    ///
    /// Named for what it is. It used to be the only URL here and it ended up in
    /// HeraldCharacter.Url as well, so every character card linked a member to a
    /// page of JSON instead of to the herald. Two URLs, two names.
    /// </summary>
    public static bool TryApiUrl(string baseUrl, string characterName, out string url)
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

    /// <summary>
    /// The herald's own page for a character, which is where a card should point.
    ///
    /// Not the API. This is the page a person reads, and the herald serves it at
    /// /player/{name} whatever the capitalisation.
    /// </summary>
    public static string? PlayerUrl(string baseUrl, string characterName) =>
        Data.ExternalUrl.TryParse(baseUrl, out var root)
        && IsPlausibleCharacterName(characterName)
            ? $"{root.TrimEnd('/')}/player/{Uri.EscapeDataString(characterName)}"
            : null;

    /// <summary>FFXI names are one alphabetic word, capped at 15 in the client.</summary>
    public static bool IsPlausibleCharacterName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 16
        && name.All(char.IsAsciiLetter);

    /// <summary>
    /// The portrait the herald renders for a character, or null when it has none.
    ///
    /// The API does not list this route; it is what the herald's own player pages
    /// use. A character with renderable false, or a missing hash, has no picture,
    /// and the herald 404s the route rather than serving a placeholder.
    ///
    /// The version is the appearance hash **and** the equipment argument, not the
    /// hash alone. The herald's notes say to re-render only where the hash changed,
    /// and taking that at face value was wrong: on 2026-08-30 the herald served two
    /// different renders of character 1 under one hash, 040480b55b00, one wearing
    /// armour and one wearing none. It even sends that hash as the ETag and marks
    /// the response immutable. So the hash tracks something narrower than the
    /// picture, and a site keyed on it alone stops updating a portrait for good.
    ///
    /// equip_arg is the models the renderer is actually given, so it changes when
    /// the equipment does. It costs nothing: it is in the record already fetched.
    /// </summary>
    public static HeraldPortrait? MapPortrait(XiCharacter dto, string baseUrl)
    {
        if (dto.Appearance is not { Renderable: true, Hash: { Length: > 0 } hash } || dto.Id <= 0)
        {
            return null;
        }

        var root = baseUrl.TrimEnd('/');

        // The URL keeps the hash, which is what the herald's own pages ask for.
        var url = $"{root}/portraits/{dto.Id}.png?v={Uri.EscapeDataString(hash)}";

        var equipment = dto.Appearance.EquipArg is { Length: > 0 } arg
            ? arg
            : string.Join(',', (dto.Appearance.Models ?? new Dictionary<string, int>())
                .OrderBy(m => m.Key, StringComparer.Ordinal)
                .Select(m => $"{m.Key}={m.Value}"));

        return new HeraldPortrait(url, $"{hash}|{equipment}");
    }

    /// <param name="playerUrl">The herald's page for this character. Never the API.</param>
    public static HeraldCharacter Map(XiCharacter dto, string? playerUrl, string baseUrl) => new()
    {
        Portrait = MapPortrait(dto, baseUrl),
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
        Url = playerUrl,
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
        public int Id { get; set; }

        public string Name { get; set; } = "";

        /// <summary>Carries the render hash and whether a portrait exists at all.</summary>
        public XiAppearance? Appearance { get; set; }
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

    /// <summary>
    /// Only the two fields that decide whether there is a picture and whether it
    /// has changed. The block also carries models, equipment and a face id, which
    /// are the renderer's business rather than ours.
    /// </summary>
    public sealed class XiAppearance
    {
        public string? Hash { get; set; }

        public bool Renderable { get; set; }

        /// <summary>
        /// What the renderer is handed, e.g. "main=94" or a full set of slots.
        ///
        /// Part of the portrait's version, because the hash beside it is not enough
        /// on its own; see MapPortrait.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("equip_arg")]
        public string? EquipArg { get; set; }

        /// <summary>The same equipment as numbers, in case equip_arg ever goes away.</summary>
        public Dictionary<string, int>? Models { get; set; }
    }
}

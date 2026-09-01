using Rmv.Web.Herald;

namespace Rmv.Web.Tests;

/// <summary>
/// Stands in for a real herald so the service's own logic can be tested without
/// touching anyone else's server.
///
/// This exists because the first version of these tests hit Blackthorn eight
/// times per run. Three runs in a minute and results started failing, which
/// looked like a bug in the service and was actually my suite being rude. Only
/// the parsing needs a real herald, and that is covered by saved fixtures and by
/// the opt-in live tests.
/// </summary>
public sealed class FakeHeraldAdapter : IHeraldAdapter
{
    public string Key => "fake";

    public string DisplayName => "Fake herald";

    public string DefaultBaseUrl => "https://fake.test";

    /// <summary>
    /// One of each kind, so a test can see a herald group in the editor's palette:
    /// a stat from the character's Stats document, and one of the shared columns
    /// named in this herald's own words.
    /// </summary>
    public IReadOnlyList<HeraldStat> Stats { get; } = HeraldStatTokens.Declare(
        new("FakeStat", "Something this herald publishes", "12"),
        new("Score", "Fake points", "1,234", SheetField.Score));

    /// <summary>Score, matching the two real heralds that publish a cumulative measure.</summary>
    public LeaderboardMetric Metric { get; set; } = new(RankBy.Score, "Realm points");

    /// <summary>Names this herald knows, case-insensitively.</summary>
    public Dictionary<string, HeraldCharacter> Known { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set to make every fetch fail, standing in for a herald being down.</summary>
    public string? ForcedError { get; set; }

    /// <summary>
    /// Set to stand in for the Armory, the one herald that admits it does not list
    /// every character. Null by default, which is every other herald.
    /// </summary>
    public string? CoverageNote { get; set; }

    public int Calls { get; private set; }

    public FakeHeraldAdapter WithCharacter(string name, Action<HeraldCharacterBuilder>? configure = null)
    {
        var builder = new HeraldCharacterBuilder(name);
        configure?.Invoke(builder);
        Known[name] = builder.Build();
        return this;
    }

    public Task<HeraldResult> FetchCharacterAsync(
        string baseUrl, string characterName, CancellationToken ct)
    {
        Calls++;

        if (ForcedError is not null)
        {
            return Task.FromResult(HeraldResult.Fail(ForcedError));
        }

        return Task.FromResult(Known.TryGetValue(characterName, out var found)
            ? HeraldResult.Found(found)
            : HeraldResult.Fail($"The herald has no character called \"{characterName}\"."));
    }

    public sealed class HeraldCharacterBuilder(string name)
    {
        public int? Level { get; set; } = 50;
        public string? Realm { get; set; } = "Hibernia";
        public string? Class { get; set; } = "Champion";
        public long? Score { get; set; } = 1234;

        /// <summary>Null by default, so a test opts in to the portrait path.</summary>
        public HeraldPortrait? Portrait { get; set; }

        public HeraldCharacter Build() => new()
        {
            Portrait = Portrait,
            // Canonical capitalisation comes back from the herald, as the real
            // ones do, so the service storing the echo rather than the input is
            // exercised.
            Name = char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant(),
            Level = Level,
            Realm = Realm,
            Class = Class,
            RealmPoints = Score,
            Url = $"https://fake.test/player/{name}",
        };
    }
}

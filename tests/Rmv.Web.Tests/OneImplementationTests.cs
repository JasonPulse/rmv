using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;
using Rmv.Web.Herald;

namespace Rmv.Web.Tests;

/// <summary>
/// The herald address, which was resolved in two places.
///
/// CharacterService had it privately and ServerStatusMonitor had its own copy. The
/// consequence of those two drifting is the worst kind of quiet: the light on the
/// home page reports a different server from the one characters are fetched from,
/// and both look like they are working.
/// </summary>
public class HeraldAddressTests
{
    private sealed class Adapter : IHeraldAdapter
    {
        public LeaderboardMetric Metric => new(RankBy.Score, "Points");
        public string Key => "test";
        public string DisplayName => "Test";
        public string DefaultBaseUrl => "https://herald.example.com";

        public Task<HeraldResult> FetchCharacterAsync(string baseUrl, string name, CancellationToken ct) =>
            Task.FromResult(HeraldResult.Fail("not used"));
    }

    private static GamePresence Game(string? overrideUrl) =>
        new() { Game = "Test", HeraldAdapterKey = "test", HeraldBaseUrl = overrideUrl };

    [Fact]
    public void The_adapters_own_address_is_the_default()
    {
        Assert.Equal("https://herald.example.com", HeraldAddress.For(Game(null), new Adapter()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_override_is_no_override(string blank)
    {
        // An admin clearing the field posts empty string, not null.
        Assert.Equal("https://herald.example.com", HeraldAddress.For(Game(blank), new Adapter()));
    }

    [Fact]
    public void An_override_wins_for_the_day_a_server_changes_domain()
    {
        Assert.Equal(
            "https://moved.example.com",
            HeraldAddress.For(Game("https://moved.example.com"), new Adapter()));
    }
}

/// <summary>
/// The URL a page points an img at, against the route that serves it.
///
/// These are two forms of one fact and cannot be a single literal, because the
/// route carries a constraint token and the path carries a value. So they live
/// beside each other in the endpoint class and this holds them together: a mismatch
/// is a broken image, which is invisible in a build and easy to miss on a page.
/// </summary>
public class ImageRouteTests
{
    /// <summary>
    /// An ASP.NET route pattern as a regex. Every token in these two routes is an
    /// int constraint, so each becomes a run of digits and the literal parts around
    /// them are matched exactly.
    /// </summary>
    private static Regex AsRegex(string pattern) =>
        new("^" + string.Join(
            "[0-9]+",
            Regex.Split(pattern, @"\{[^}]+\}").Select(Regex.Escape)) + "$");

    [Fact]
    public void A_screenshot_path_is_served_by_the_screenshot_route()
    {
        var path = ScreenshotEndpoint.PathFor(42);

        Assert.Equal("/gallery/42/image", path);
        Assert.Matches(AsRegex(ScreenshotEndpoint.Route), path);
    }

    [Fact]
    public void A_portrait_path_is_served_by_the_portrait_route()
    {
        var path = PortraitEndpoint.PathFor(42, "abc123");

        // The version is a query string, which the route does not match on.
        var withoutQuery = path.Split('?')[0];

        Assert.Equal("/characters/42/portrait", withoutQuery);
        Assert.Matches(AsRegex(PortraitEndpoint.Route), withoutQuery);
    }

    [Fact]
    public void The_model_properties_use_the_endpoints_own_builders()
    {
        var shot = new Screenshot { Id = 7 };
        Assert.Equal(ScreenshotEndpoint.PathFor(7), shot.Path);

        var character = new Character { Id = 7, PortraitVersion = "v1" };
        Assert.Equal(PortraitEndpoint.PathFor(7, "v1"), character.PortraitPath);

        // No stored portrait is no URL, rather than a URL that 404s.
        Assert.Null(new Character { Id = 7 }.PortraitPath);
    }

    [Fact]
    public void A_version_that_needs_encoding_is_encoded()
    {
        // The digest never contains one, but the property is public and the query
        // string is a place a stray character breaks a URL rather than a build.
        Assert.Contains("v=a%20b%26c", PortraitEndpoint.PathFor(1, "a b&c"));
    }
}

/// <summary>
/// Limits that appear in a form attribute, a service and a column width are one
/// limit. These read them from one constant; this is the check that they still do.
/// </summary>
public class LimitAgreementTests
{
    [Fact]
    public void The_character_form_and_the_service_share_their_limits()
    {
        var edit = typeof(Rmv.Web.Pages.Characters.IndexModel.EditModel);
        var add = typeof(Rmv.Web.Pages.Characters.IndexModel.InputModel);

        Assert.Equal(CharacterLimits.MaxName, MaxLength(edit, "Name"));
        Assert.Equal(CharacterLimits.MaxTyped, MaxLength(add, "Name"));
        Assert.Equal(CharacterLimits.MaxClass, MaxLength(add, "Class"));

        var level = add.GetProperty("Level")!
            .GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.RangeAttribute), true)
            .Cast<System.ComponentModel.DataAnnotations.RangeAttribute>()
            .Single();

        Assert.Equal(CharacterLimits.MinLevel, level.Minimum);
        Assert.Equal(CharacterLimits.MaxLevel, level.Maximum);
    }

    private static int MaxLength(Type model, string property) =>
        model.GetProperty(property)!
            .GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.StringLengthAttribute), true)
            .Cast<System.ComponentModel.DataAnnotations.StringLengthAttribute>()
            .Single()
            .MaximumLength;

    [Fact]
    public void The_request_log_truncates_to_the_width_of_its_columns()
    {
        using var context = new RmvDbContext(
            new DbContextOptionsBuilder<RmvDbContext>()
                .UseNpgsql("Host=none;Database=none")
                .Options);

        var entity = context.Model.FindEntityType(typeof(RequestLog))!;

        foreach (var column in new[] { "Path", "Referrer", "UserAgent" })
        {
            Assert.Equal(
                Rmv.Web.Analytics.RequestLogMiddleware.MaxTextLength,
                entity.FindProperty(column)!.GetMaxLength());
        }

        Assert.Equal(
            Rmv.Web.Analytics.RequestLogMiddleware.MaxHostLength,
            entity.FindProperty("ReferrerHost")!.GetMaxLength());
    }

    [Fact]
    public void The_character_columns_are_as_wide_as_the_limits()
    {
        using var context = new RmvDbContext(
            new DbContextOptionsBuilder<RmvDbContext>()
                .UseNpgsql("Host=none;Database=none")
                .Options);

        var entity = context.Model.FindEntityType(typeof(Character))!;

        Assert.Equal(CharacterLimits.MaxName, entity.FindProperty("Name")!.GetMaxLength());
        Assert.Equal(CharacterLimits.MaxClass, entity.FindProperty("Class")!.GetMaxLength());
    }
}

/// <summary>
/// The one capped read, which is the real enforcement of three memory limits: an
/// upload, a portrait and a herald page. The boundary is the whole point, so it is
/// tested at the boundary rather than with a round number.
/// </summary>
public class CappedReadTests
{
    /// <summary>Yields a few bytes at a time, as a network stream does.</summary>
    private sealed class DribbleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, chunk));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], ct);
    }

    [Fact]
    public async Task Exactly_at_the_cap_is_allowed()
    {
        var bytes = await Rmv.Web.CappedRead.AllAsync(new MemoryStream(new byte[100]), 100, default);

        Assert.NotNull(bytes);
        Assert.Equal(100, bytes.Length);
    }

    [Fact]
    public async Task One_byte_over_the_cap_is_refused()
    {
        Assert.Null(await Rmv.Web.CappedRead.AllAsync(new MemoryStream(new byte[101]), 100, default));
    }

    [Fact]
    public async Task An_empty_stream_reads_as_empty_rather_than_refused()
    {
        // Whether empty is acceptable is the caller's rule: the gallery refuses it,
        // a portrait reports it, and neither is this method's decision.
        var bytes = await Rmv.Web.CappedRead.AllAsync(new MemoryStream([]), 100, default);

        Assert.NotNull(bytes);
        Assert.Empty(bytes);
    }

    [Fact]
    public async Task The_cap_holds_across_many_small_reads()
    {
        // The shape that matters. One read never exceeds the cap on its own, so a
        // check that looked at the chunk instead of the running total would pass
        // everything through.
        var data = new byte[10_000];
        var bytes = await Rmv.Web.CappedRead.AllAsync(new DribbleStream(data, 7), 9_999, default);

        Assert.Null(bytes);
    }

    [Fact]
    public async Task Everything_arrives_when_it_fits()
    {
        var data = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();

        var bytes = await Rmv.Web.CappedRead.AllAsync(new DribbleStream(data, 13), 5000, default);

        Assert.Equal(data, bytes);
    }
}

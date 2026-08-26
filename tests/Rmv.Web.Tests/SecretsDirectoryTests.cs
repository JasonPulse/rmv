using Microsoft.Extensions.Configuration;
using Rmv.Web.Configuration;

namespace Rmv.Web.Tests;

public class SecretsDirectoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rmv-secrets-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_dir, name), content);

    private IConfigurationRoot Build(params (string Key, string Value)[] envLike)
    {
        var builder = new ConfigurationBuilder();
        if (envLike.Length > 0)
        {
            builder.AddInMemoryCollection(
                envLike.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)));
        }

        return builder.AddSecretsDirectory(_dir).Build();
    }

    [Fact]
    public void Maps_a_double_underscore_filename_to_a_nested_key()
    {
        Write("ConnectionStrings__Postgres", "Host=db;Database=rmv");

        Assert.Equal("Host=db;Database=rmv", Build()["ConnectionStrings:Postgres"]);
    }

    [Fact]
    public void A_non_empty_secret_beats_an_environment_variable()
    {
        Write("Discord__ClientId", "from-secret");

        var config = Build(("Discord:ClientId", "from-env"));

        Assert.Equal("from-secret", config["Discord:ClientId"]);
    }

    [Fact]
    public void An_empty_secret_does_not_mask_an_environment_variable()
    {
        // The bug this replaced AddKeyPerFile for. An empty key in a Kubernetes
        // Secret made a configured env var look unset, with nothing in the logs,
        // and the Discord sign-in button silently never rendered.
        Write("Discord__ClientId", "");

        var config = Build(("Discord:ClientId", "from-env"));

        Assert.Equal("from-env", config["Discord:ClientId"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    [InlineData("\r\n\t ")]
    public void Whitespace_only_secrets_are_ignored_entirely(string content)
    {
        Write("Some__Key", content);

        Assert.Null(Build()["Some:Key"]);
    }

    [Fact]
    public void Trailing_whitespace_is_trimmed()
    {
        // Hand-written secret files usually end with a newline.
        Write("Discord__ClientSecret", "abc123\n");

        Assert.Equal("abc123", Build()["Discord:ClientSecret"]);
    }

    [Fact]
    public void Skips_the_kubernetes_atomic_update_plumbing()
    {
        // Real mounts contain ..data and ..2026_01_01_... alongside the keys.
        Write(".hidden", "ignored");
        Write("Real__Key", "kept");

        var config = Build();

        Assert.Equal("kept", config["Real:Key"]);
        Assert.Null(config[".hidden"]);
    }

    [Fact]
    public void A_missing_directory_is_not_an_error()
    {
        var config = new ConfigurationBuilder()
            .AddSecretsDirectory(Path.Combine(_dir, "does-not-exist"))
            .Build();

        Assert.Empty(config.AsEnumerable());
    }
}

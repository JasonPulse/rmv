namespace Rmv.Web.Tests;

/// <summary>
/// The database and network tests share one Postgres, so they must not run in
/// parallel with each other.
///
/// They were flaky without this: xUnit runs test classes concurrently, so two
/// classes called MigrateAsync on the same fresh database at once and four tests
/// failed on the first run and passed on the second. A test that passes on
/// retry is worse than one that fails, because it teaches you to rerun.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NetworkCollection
{
    public const string Name = "Network";
}

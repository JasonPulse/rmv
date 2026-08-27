namespace Rmv.Web.Herald;

/// <summary>
/// Looks an adapter up by the key stored against a game. Registering an adapter
/// is the only code change a new server needs; the URL is data.
/// </summary>
public sealed class HeraldRegistry(IEnumerable<IHeraldAdapter> adapters)
{
    private readonly Dictionary<string, IHeraldAdapter> _byKey =
        adapters.ToDictionary(a => a.Key, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IHeraldAdapter> All => _byKey.Values;

    public IHeraldAdapter? Find(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : _byKey.GetValueOrDefault(key);
}

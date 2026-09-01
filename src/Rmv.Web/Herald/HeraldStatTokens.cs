namespace Rmv.Web.Herald;

/// <summary>
/// Every stat every registered herald publishes, gathered once.
///
/// Two callers, and both need the whole set rather than one herald's:
///
///   The signature editor's palette, which offers them grouped by herald so a
///   member can see that relics are a DAoC thing and master level is not.
///   The token resolver, which needs to tell "a stat from a herald this line's
///   character is not on", which draws nothing, from "a typo", which stays visible.
///
/// Built from the adapters rather than listed here, so adding a stat to a herald is
/// one line in that herald.
/// </summary>
public sealed class HeraldStatTokens(HeraldRegistry heralds)
{
    /// <param name="Herald">The adapter's display name, for the palette's heading.</param>
    public sealed record Group(string Herald, IReadOnlyList<HeraldStat> Stats);

    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Grouped by herald, in registration order, skipping the empty ones.</summary>
    public IReadOnlyList<Group> All => heralds.All
        .Where(a => a.Stats.Count > 0)
        .Select(a => new Group(a.DisplayName, a.Stats))
        .ToList();

    /// <summary>
    /// Whether any herald publishes a stat by this name.
    ///
    /// Static, and populated as adapters are constructed, because the resolver is
    /// pure and static and has no registry to ask. The set only grows, and every
    /// adapter is built at startup, so by the time anything renders it is complete.
    /// </summary>
    public static bool IsKnown(string key)
    {
        lock (KnownKeys)
        {
            return KnownKeys.Contains(key);
        }
    }

    /// <summary>Called by an adapter to declare its stats. Idempotent.</summary>
    public static IReadOnlyList<HeraldStat> Declare(params HeraldStat[] stats)
    {
        lock (KnownKeys)
        {
            foreach (var stat in stats)
            {
                KnownKeys.Add(stat.Key);
            }
        }

        return stats;
    }
}

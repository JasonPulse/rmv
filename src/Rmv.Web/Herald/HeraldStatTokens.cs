namespace Rmv.Web.Herald;

/// <summary>
/// What the registered heralds publish, for the two callers that need more than one
/// herald's worth:
///
///   The signature editor's palette, which offers them grouped by herald so a
///   member can see that relics are a DAoC thing and master level is not, and only
///   for the heralds that member has a character on.
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

    /// <summary>
    /// The groups for the heralds a member actually has a character on, in
    /// registration order.
    ///
    /// Filtered rather than complete, because a member with no WoW character has no
    /// use for %ItemLevel%: it would draw nothing whatever line it went on, and a
    /// palette of tokens that cannot work is a palette nobody trusts. Adding a
    /// character to a game makes that game's stats appear.
    /// </summary>
    /// <param name="adapterKeys">
    /// The herald keys of the games the member has characters on, from
    /// GamePresence.HeraldAdapterKey. Nulls and unknown keys are ignored, which is
    /// what a game with no herald and a game whose adapter was removed look like.
    /// </param>
    public IReadOnlyList<Group> For(IEnumerable<string?> adapterKeys)
    {
        ArgumentNullException.ThrowIfNull(adapterKeys);

        var wanted = adapterKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return heralds.All
            .Where(a => a.Stats.Count > 0 && wanted.Contains(a.Key))
            .Select(a => new Group(a.DisplayName, a.Stats))
            .ToList();
    }

    /// <summary>
    /// Whether any herald publishes a document stat by this name.
    ///
    /// Only used to tell a stat from a herald this character is not on, which draws
    /// nothing, from a typo, which stays visible. Not used for values: adapters are
    /// scoped services and a stored render is served without building one, so a set
    /// that fills up as adapters are constructed cannot be asked what anything is
    /// worth. The shared columns are named in SheetColumns for the same reason.
    /// </summary>
    public static bool IsKnown(string key)
    {
        lock (KnownKeys)
        {
            return KnownKeys.Contains(key);
        }
    }

    /// <summary>
    /// Called by an adapter to declare its stats. Idempotent.
    ///
    /// Throws when a herald points a shared column's name at a different column,
    /// which would be one token name with two meanings. Adapters are built on the
    /// first request that needs one, and the test suite builds all four, so a
    /// mistake here surfaces immediately.
    /// </summary>
    public static IReadOnlyList<HeraldStat> Declare(params HeraldStat[] stats)
    {
        ArgumentNullException.ThrowIfNull(stats);

        foreach (var stat in stats)
        {
            var column = SheetColumns.Field(stat.Key);

            if (stat.From != column && (stat.From != SheetField.Document || column is not null))
            {
                throw new InvalidOperationException(
                    $"%{stat.Key}% reads {column?.ToString() ?? "the stats document"} "
                    + $"and this herald declares it as {stat.From}. A shared column's "
                    + "name means the same thing on every herald; only the label is "
                    + "each herald's own.");
            }
        }

        lock (KnownKeys)
        {
            foreach (var stat in stats.Where(s => s.From == SheetField.Document))
            {
                KnownKeys.Add(stat.Key);
            }
        }

        return stats;
    }
}

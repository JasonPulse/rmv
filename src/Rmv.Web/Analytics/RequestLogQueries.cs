using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

namespace Rmv.Web.Analytics;

/// <summary>One row of a "top N by count" panel.</summary>
public sealed record Count(string Key, int Total);

/// <summary>One row of a status code breakdown.</summary>
public sealed record StatusCount(int Status, int Total);

/// <summary>
/// The one grouped query every analytics panel is. Both the overview and the
/// per-path page had their own copy, differing only in whether they reported nulls.
/// </summary>
public static class RequestLogQueries
{
    /// <summary>
    /// Top <paramref name="take"/> values of one column by hit count.
    ///
    /// The anonymous type in the middle is not incidental. Projecting straight into
    /// a record constructor inside a GroupBy is not translatable and throws at
    /// runtime rather than at compile time, so the shape has to be: group and count
    /// in the database, map to the record afterwards.
    /// </summary>
    /// <param name="whenNull">
    /// What a null groups as. Reported rather than dropped, because for a ten year
    /// old signature URL "every one of these arrived with no referrer" is the
    /// finding, not an absence of data. Callers that have already filtered nulls out
    /// never see it.
    /// </param>
    public static async Task<IReadOnlyList<Count>> TopAsync(
        IQueryable<RequestLog> rows,
        Expression<Func<RequestLog, string?>> by,
        int take,
        CancellationToken ct,
        string whenNull = "(none)") =>
        (await rows
                .GroupBy(by)
                .Select(g => new { Key = g.Key, Total = g.Count() })
                .OrderByDescending(x => x.Total)
                .Take(take)
                .ToListAsync(ct))
            .Select(x => new Count(x.Key ?? whenNull, x.Total))
            .ToList();

    /// <summary>Hit count per status code, most common first.</summary>
    public static async Task<IReadOnlyList<StatusCount>> StatusesAsync(
        IQueryable<RequestLog> rows, CancellationToken ct) =>
        (await rows
                .GroupBy(r => r.Status)
                .Select(g => new { Key = g.Key, Total = g.Count() })
                .OrderByDescending(x => x.Total)
                .ToListAsync(ct))
            .Select(x => new StatusCount(x.Key, x.Total))
            .ToList();
}

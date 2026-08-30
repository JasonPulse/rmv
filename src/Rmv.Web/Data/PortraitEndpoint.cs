using Microsoft.EntityFrameworkCore;

namespace Rmv.Web.Data;

/// <summary>
/// Serves a stored character portrait.
///
/// This exists because the FFXI herald is internal. It resolves to an RFC1918
/// address, so a visitor's browser cannot fetch its portraits at all; only the pod
/// can, and only because the operator allowlist permits it. The bytes are fetched
/// server-side on add and refresh, stored, and served from here.
///
/// It is not a proxy. It never takes a URL from the request, only a character id,
/// and it returns only bytes already stored against that character. There is no
/// input here that could point it at something else.
///
/// Public, like the roster it appears on, with one exception carried over from
/// that page: a blocked member is off the roster entirely and their characters go
/// with them, so their portraits 404 as well.
/// </summary>
public static class PortraitEndpoint
{
    /// <summary>
    /// The route, and the path a page points an img at. Both here so a change to
    /// one is a change to the other in view; ImageRouteTests holds them together.
    ///
    /// The version is in the query so the browser refetches when the picture
    /// changes and never otherwise.
    /// </summary>
    public const string Route = "/characters/{id:int}/portrait";

    public static string PathFor(int id, string version) =>
        $"/characters/{id}/portrait?v={Uri.EscapeDataString(version)}";

    public static void MapPortraits(this WebApplication app)
    {
        app.MapGet(Route, async (
            int id,
            RmvDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            // Projected, so the 120KB of bytes are not loaded to answer a
            // conditional request that will not send them.
            var meta = await db.Characters
                .Where(c => c.Id == id)
                // Off the roster is off the site. One rule, in RosterVisibility.
                .OnRoster()
                .Select(c => new { c.PortraitVersion })
                .FirstOrDefaultAsync(ct);

            if (meta?.PortraitVersion is null)
            {
                return Results.NotFound();
            }

            var etag = StoredImage.ETagFor(meta.PortraitVersion);

            // The version changes only when the picture does, so a match means the
            // browser's copy is still correct. Answering 304 here is most of the
            // point of keying the URL on the version.
            if (StoredImage.AlreadyHas(http, etag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            var portrait = await db.CharacterPortraits
                .Where(p => p.CharacterId == id)
                .Select(p => new { p.Bytes, p.ContentType })
                .FirstOrDefaultAsync(ct);

            return StoredImage.Bytes(http, portrait?.Bytes, portrait?.ContentType ?? "image/png", etag);
        })
        .WithName("CharacterPortrait")
        .AllowAnonymous();
    }
}

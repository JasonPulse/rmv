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
    public static void MapPortraits(this WebApplication app)
    {
        app.MapGet("/characters/{id:int}/portrait", async (
            int id,
            RmvDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            // Projected, so the 120KB of bytes are not loaded to answer a
            // conditional request that will not send them.
            var meta = await db.Characters
                .Where(c => c.Id == id)
                .Select(c => new
                {
                    c.PortraitVersion,
                    Blocked = c.Member != null && c.Member.Status == MemberStatus.Blocked,
                })
                .FirstOrDefaultAsync(ct);

            if (meta is null || meta.Blocked || meta.PortraitVersion is null)
            {
                return Results.NotFound();
            }

            var etag = $"\"{meta.PortraitVersion}\"";

            // The version changes only when the picture does, so a match means the
            // browser's copy is still correct. Answering 304 here is most of the
            // point of keying the URL on the version.
            if (http.Request.Headers.IfNoneMatch.Any(v => v == etag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            var portrait = await db.CharacterPortraits
                .Where(p => p.CharacterId == id)
                .Select(p => new { p.Bytes, p.ContentType })
                .FirstOrDefaultAsync(ct);

            if (portrait is null || portrait.Bytes.Length == 0)
            {
                // A version with no bytes means a refresh was interrupted between
                // the two writes. Reporting it as absent is honest, and the next
                // refresh will fill it in.
                return Results.NotFound();
            }

            // A year, because the URL carries the version: a different picture is a
            // different URL, so this response can never be the wrong one. Private
            // is wrong here, this is public art on a public page.
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";

            return Results.Bytes(portrait.Bytes, portrait.ContentType, entityTag: new(etag));
        })
        .WithName("CharacterPortrait")
        .AllowAnonymous();
    }
}

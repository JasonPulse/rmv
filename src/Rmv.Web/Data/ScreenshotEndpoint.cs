using Microsoft.EntityFrameworkCore;

namespace Rmv.Web.Data;

/// <summary>
/// Serves a screenshot's bytes.
///
/// Public, like the gallery page it appears on. Not a proxy: it takes an id and
/// returns only bytes already stored against it, never a URL from the request.
///
/// The content type comes from what ImageProbe read out of the file when it was
/// stored, so it can only ever be one of four image types. That is the reason the
/// probe exists: this endpoint echoes the stored type, and a file that talked its
/// way in claiming image/png while containing HTML would be served as HTML from our
/// own origin.
/// </summary>
public static class ScreenshotEndpoint
{
    public static void MapScreenshots(this WebApplication app)
    {
        app.MapGet("/gallery/{id:int}/image", async (
            int id,
            RmvDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            // Projected, so a conditional request that will not send the image does
            // not load it out of Postgres to find that out.
            var meta = await db.Screenshots
                .Where(s => s.Id == id)
                .Select(s => new
                {
                    s.ContentType,
                    s.UploadedAt,
                    Blocked = s.Member != null && s.Member.Status == MemberStatus.Blocked,
                })
                .FirstOrDefaultAsync(ct);

            // A blocked member is off the roster and their characters go with them,
            // so their screenshots do too.
            if (meta is null || meta.Blocked)
            {
                return Results.NotFound();
            }

            // The bytes never change once stored, so the upload time is a complete
            // version: a different picture is a different id.
            var etag = $"\"{meta.UploadedAt.ToUnixTimeMilliseconds()}\"";

            if (http.Request.Headers.IfNoneMatch.Any(v => v == etag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            var bytes = await db.ScreenshotImages
                .Where(i => i.ScreenshotId == id)
                .Select(i => i.Bytes)
                .FirstOrDefaultAsync(ct);

            if (bytes is null || bytes.Length == 0)
            {
                return Results.NotFound();
            }

            // A year: an id maps to one immutable image for as long as it exists.
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";

            return Results.Bytes(bytes, meta.ContentType, entityTag: new(etag));
        })
        .WithName("Screenshot")
        .AllowAnonymous();
    }
}

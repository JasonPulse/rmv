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
    /// <summary>
    /// The route, and the path a page points an img at. Both here so a change to
    /// one is a change to the other in view; ImageRouteTests holds them together.
    /// </summary>
    public const string Route = "/gallery/{id:int}/image";

    public static string PathFor(int id) => $"/gallery/{id}/image";

    public static void MapScreenshots(this WebApplication app)
    {
        app.MapGet(Route, async (
            int id,
            RmvDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            // Projected, so a conditional request that will not send the image does
            // not load it out of Postgres to find that out.
            var meta = await db.Screenshots
                .Where(s => s.Id == id)
                // Off the roster is off the site: not found, rather than found and
                // then refused. One rule, in RosterVisibility.
                .OnRoster()
                .Select(s => new { s.ContentType, s.UploadedAt })
                .FirstOrDefaultAsync(ct);

            if (meta is null)
            {
                return Results.NotFound();
            }

            // The bytes never change once stored, so the upload time is a complete
            // version: a different picture is a different id.
            var etag = StoredImage.ETagFor(
                meta.UploadedAt.ToUnixTimeMilliseconds().ToString());

            if (StoredImage.AlreadyHas(http, etag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            var bytes = await db.ScreenshotImages
                .Where(i => i.ScreenshotId == id)
                .Select(i => i.Bytes)
                .FirstOrDefaultAsync(ct);

            return StoredImage.Bytes(http, bytes, meta.ContentType, etag);
        })
        .WithName("Screenshot")
        .AllowAnonymous();
    }
}

namespace Rmv.Web.Data;

/// <summary>
/// How stored bytes are served: the ETag, the conditional check, and the caching.
///
/// Portraits and screenshots are the same mechanism with different tables. Both
/// endpoints had their own copy of the ETag format, the If-None-Match comparison
/// and the Cache-Control string, which is one rule about caching written twice. The
/// failure would be quiet and awkward: change the header on one and a visitor gets
/// a year-long cache of one kind of image and a revalidation on the other, with
/// nothing on screen to say so.
/// </summary>
public static class StoredImage
{
    /// <summary>
    /// A year. Both URLs carry a version, and an id maps to one immutable image, so
    /// a response here can never turn out to be the wrong one.
    /// </summary>
    public const string CacheControl = "public, max-age=31536000, immutable";

    /// <summary>Quoted, which is what an entity tag is.</summary>
    public static string ETagFor(string version) => $"\"{version}\"";

    /// <summary>Whether the caller already has this exact version.</summary>
    public static bool AlreadyHas(HttpContext http, string etag) =>
        http.Request.Headers.IfNoneMatch.Any(v => v == etag);

    /// <summary>
    /// The bytes, or 404 when there are none.
    ///
    /// Absent bytes against a row that claims a picture means a write was
    /// interrupted between the two. Reporting it as absent is honest, and the next
    /// refresh fills it in.
    /// </summary>
    public static IResult Bytes(HttpContext http, byte[]? bytes, string contentType, string etag)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return Results.NotFound();
        }

        http.Response.Headers.CacheControl = CacheControl;

        return Results.Bytes(bytes, contentType, entityTag: new(etag));
    }
}

using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

namespace Rmv.Web.Signature;

/// <summary>
/// Serves a rendered signature to whatever forum embedded it.
///
/// This is the hot path, and the only one on the site that strangers hit in bulk, so
/// it is the shape of the whole feature:
///
///   One indexed read of stored bytes. No render, no herald, no template parsing.
///   An ETag, so a browser that already has it gets 304 and no bytes.
///   Cache-Control with a real max-age, so Cloudflare answers most of it and the
///   homelab never sees the request at all.
///   Rate limited per address, as the backstop for anything pathological.
///
/// The old one did the opposite of all four: sig.php fetched the herald over the
/// network, scraped it with eight regexes, rendered with GD, and sent
/// "Cache-Control: no-cache, must-revalidate" with an Expires date in 1997. Ten
/// signatures in one forum page were ten scrapes and ten renders, every view, and
/// v2 had to start sniffing user agents for "bot" to survive being crawled.
///
/// Public by necessity. A forum sends no cookies, so this cannot be behind a
/// sign-in; that is why the address is an opaque slug rather than a member id, and
/// why a blocked member's signature stops resolving.
/// </summary>
public static class SignatureEndpoint
{
    public const string Route = "/sig/{slug}.png";

    public static string PathFor(string slug) => $"/sig/{slug}.png";

    /// <summary>
    /// Fifteen minutes.
    ///
    /// The number is a tradeoff and this is the reasoning: stats change on a daily
    /// pass, so a signature is never more than a few minutes behind what anybody
    /// could see anyway, and an edge cache with a fifteen minute window absorbs
    /// essentially all forum traffic. Shorter would put the traffic back on the
    /// homelab for no freshness anybody notices. stale-while-revalidate lets the
    /// edge serve the old one while it fetches, so nobody waits on us.
    /// </summary>
    private const string Caching = "public, max-age=900, stale-while-revalidate=3600";

    public static void MapSignatures(this WebApplication app)
    {
        app.MapGet(Route, async (
            string slug,
            RmvDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            // Bounded before it reaches the database. A slug is twelve characters and
            // anything else is not one.
            if (slug.Length != Data.Signature.SlugLength)
            {
                return Results.NotFound();
            }

            // Projected, so a request that will answer 304 does not load the PNG out
            // of Postgres to find that out. The member's status comes along because a
            // blocked member is off the roster and their signature goes with them.
            var meta = await db.Signatures
                .Where(s => s.Slug == slug)
                .Select(s => new
                {
                    s.Id,
                    Version = s.Image!.Version,
                    Visible = s.Member != null
                              && RosterVisibility.Visible.Contains(s.Member.Status),
                })
                .FirstOrDefaultAsync(ct);

            if (meta is null || !meta.Visible || string.IsNullOrEmpty(meta.Version))
            {
                return Results.NotFound();
            }

            var etag = StoredImage.ETagFor(meta.Version);

            if (StoredImage.AlreadyHas(http, etag))
            {
                // The cheapest possible answer, and the one most requests get.
                http.Response.Headers.CacheControl = Caching;
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            var bytes = await db.SignatureImages
                .Where(i => i.SignatureId == meta.Id)
                .Select(i => i.Bytes)
                .FirstOrDefaultAsync(ct);

            if (bytes is null || bytes.Length == 0)
            {
                return Results.NotFound();
            }

            // Not StoredImage.Bytes: a portrait's URL carries its version so it can be
            // immutable for a year, and this URL cannot. A forum post embeds one
            // address forever, so freshness has to come from revalidation.
            http.Response.Headers.CacheControl = Caching;

            return Results.Bytes(bytes, "image/png", entityTag: new(etag));
        })
        .WithName("Signature")
        .AllowAnonymous()
        .RequireRateLimiting(Tools.RateLimitPolicies.Signature);
    }
}

using System.Text;

namespace Rmv.Web.Herald;

public sealed record FetchResult(bool Ok, string? Body, string? Error, int? StatusCode = null)
{
    public static FetchResult Fail(string error, int? status = null) => new(false, null, error, status);
    public static FetchResult Success(string body) => new(true, body, null, 200);

    /// <summary>
    /// Both heralds answer 404 for a name they do not know, so an adapter can turn
    /// this into "no such character" instead of leaking a status code at someone
    /// who simply mistyped.
    /// </summary>
    public bool NotFound => StatusCode == 404;
}

public sealed record ImageResult(bool Ok, byte[]? Bytes, string? ContentType, string? Error)
{
    public static ImageResult Fail(string error) => new(false, null, null, error);
}

/// <summary>
/// Fetches a herald page. Every limit here exists because the target is someone
/// else's server, reached via a URL an admin typed.
/// </summary>
public sealed class HeraldFetcher(HttpClient client, ILogger<HeraldFetcher> log)
{
    /// <summary>Heralds are HTML pages. Anything much larger is not one.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>
    /// A portrait is a render of one character. The two heralds serve 121KB and
    /// 114KB; a megabyte is generous and still rules out being handed a video.
    /// </summary>
    public const int MaxImageBytes = 1024 * 1024;

    /// <summary>
    /// What we will store and hand back to a browser.
    ///
    /// An allowlist because the endpoint echoes this Content-Type. Letting a
    /// herald choose it freely would let it serve text/html from our own origin,
    /// which is a stored cross-site scripting hole wearing an img tag. SVG is
    /// excluded for the same reason: it is a document that can carry script.
    /// </summary>
    private static readonly string[] ImageTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"];

    /// <summary>
    /// Fetches a character page, with the failure already turned into the message a
    /// member should see.
    ///
    /// Every adapter did this itself and they were the same five lines: a 404 means
    /// the name is wrong, which is the common case and worth saying plainly rather
    /// than reporting a status code at someone who simply mistyped.
    ///
    /// Returns the body on success, or the failure to hand straight back.
    /// </summary>
    public async Task<(string? Body, HeraldResult? Failure)> GetForCharacterAsync(
        string url, string characterName, CancellationToken ct)
    {
        var fetched = await GetAsync(url, ct);

        if (fetched.Ok)
        {
            return (fetched.Body!, null);
        }

        return (null, fetched.NotFound
            ? HeraldResult.Fail($"The herald has no character called \"{characterName}\".")
            : HeraldResult.Fail(fetched.Error ?? "Could not reach the herald."));
    }

    /// <summary>
    /// Fetches an image, capped and type-checked.
    ///
    /// Same SSRF-guarded handler as everything else here, which is the point: the
    /// FFXI herald's portraits are on an internal host, so this is the only way to
    /// reach them at all, and the operator allowlist is what permits it.
    /// </summary>
    public async Task<ImageResult> GetImageAsync(string url, CancellationToken ct)
    {
        if (!Data.ExternalUrl.TryParse(url, out var safe))
        {
            return ImageResult.Fail("Not an absolute http or https URL.");
        }

        try
        {
            using var response = await client.GetAsync(safe, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                return ImageResult.Fail($"Herald returned {(int)response.StatusCode}.");
            }

            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!ImageTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
            {
                return ImageResult.Fail($"Not an image we serve: {(type.Length == 0 ? "no content type" : type)}.");
            }

            if (response.Content.Headers.ContentLength is > MaxImageBytes)
            {
                return ImageResult.Fail("Portrait is too large.");
            }

            // Capped while reading as well: a declared length is a hint, not a
            // promise, and this one comes from someone else's server.
            await using var stream = await response.Content.ReadAsStreamAsync(ct);

            if (await CappedRead.AllAsync(stream, MaxImageBytes, ct) is not { } body)
            {
                return ImageResult.Fail("Portrait is too large.");
            }

            return body.Length == 0
                ? ImageResult.Fail("Portrait was empty.")
                : new ImageResult(true, body, type.ToLowerInvariant(), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Portrait fetch failed for {Url}.", safe);
            return ImageResult.Fail($"Could not fetch the portrait: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// Asks whether a server is answering, and how fast, without reading its body.
    ///
    /// A status check runs on a timer against someone else's server, so downloading
    /// a page every time to learn one bit would be rude and pointless. This opens
    /// the response, takes the status line and the elapsed time, and drops the rest.
    /// </summary>
    public async Task<(bool Ok, int Ms, string? Error)> PingAsync(string url, CancellationToken ct)
    {
        if (!Data.ExternalUrl.TryParse(url, out var safe))
        {
            return (false, 0, "Not an absolute http or https URL.");
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            using var response = await client.GetAsync(
                safe, HttpCompletionOption.ResponseHeadersRead, ct);

            var ms = (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            // Any answer at all means the server is up. A 403 or a 404 on the front
            // page is a configuration question, not an outage.
            return response.StatusCode < System.Net.HttpStatusCode.InternalServerError
                ? (true, ms, null)
                : (false, ms, $"Answered {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var ms = (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return (false, ms, ex.GetBaseException().Message);
        }
    }

    public async Task<FetchResult> GetAsync(string url, CancellationToken ct)
    {
        if (!Data.ExternalUrl.TryParse(url, out var safe))
        {
            return FetchResult.Fail("Not an absolute http or https URL.");
        }

        try
        {
            using var response = await client.GetAsync(
                safe, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                return FetchResult.Fail(
                    $"Herald returned {(int)response.StatusCode}.", (int)response.StatusCode);
            }

            // Declared length is a hint, not a promise, so the read below is
            // capped as well. This just avoids starting a pointless download.
            if (response.Content.Headers.ContentLength is > MaxBytes)
            {
                return FetchResult.Fail("Herald response is too large.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);

            if (await CappedRead.AllAsync(stream, MaxBytes, ct) is not { } body)
            {
                return FetchResult.Fail("Herald response is too large.");
            }

            // Heralds are frequently not valid UTF-8; replacement characters are
            // preferable to throwing on a stray byte.
            return FetchResult.Success(
                new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(body));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Includes the connect callback refusing a private address, which is
            // a configuration mistake worth surfacing rather than hiding.
            log.LogWarning(ex, "Herald fetch failed for {Url}.", safe);
            return FetchResult.Fail($"Could not reach the herald: {ex.GetBaseException().Message}");
        }
    }
}

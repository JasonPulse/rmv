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

/// <summary>
/// Fetches a herald page. Every limit here exists because the target is someone
/// else's server, reached via a URL an admin typed.
/// </summary>
public sealed class HeraldFetcher(HttpClient client, ILogger<HeraldFetcher> log)
{
    /// <summary>Heralds are HTML pages. Anything much larger is not one.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

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
            var buffer = new byte[8192];
            var read = 0;
            var total = 0;
            var body = new MemoryStream();

            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > MaxBytes)
                {
                    return FetchResult.Fail("Herald response is too large.");
                }

                body.Write(buffer, 0, read);
            }

            // Heralds are frequently not valid UTF-8; replacement characters are
            // preferable to throwing on a stray byte.
            return FetchResult.Success(
                new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(body.ToArray()));
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

namespace Rmv.Web;

/// <summary>
/// Reads a stream to a hard cap, checked as it goes.
///
/// Three copies of this loop existed: uploads in GalleryService, portraits and
/// herald pages in HeraldFetcher. Each reads from somewhere that is not ours to
/// trust, either a browser or someone else's server, and each was the actual
/// enforcement of a memory limit. A declared Content-Length is a hint and this is
/// the check that holds, which makes it the worst of the three to keep copies of:
/// a "greater or equal" that should be "greater" in one of them is invisible until
/// something large arrives.
/// </summary>
public static class CappedRead
{
    /// <summary>
    /// Everything the stream yields, or null if that would exceed
    /// <paramref name="maxBytes"/>.
    ///
    /// Null rather than an exception, because every caller has its own way of
    /// reporting "too big" to whoever asked and none of them treat it as a fault.
    /// </summary>
    public static async Task<byte[]?> AllAsync(Stream content, int maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var buffer = new byte[64 * 1024];
        var into = new MemoryStream();
        var total = 0;
        int read;

        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                return null;
            }

            into.Write(buffer, 0, read);
        }

        return into.ToArray();
    }
}

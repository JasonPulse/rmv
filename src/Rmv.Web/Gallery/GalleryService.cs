using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

namespace Rmv.Web.Gallery;

public sealed record UploadOutcome(bool Ok, Screenshot? Screenshot, string? Error)
{
    public static UploadOutcome Fail(string error) => new(false, null, error);
}

/// <summary>
/// Adding and removing screenshots.
///
/// Every check here is on the server. A form that limits a caption to 200
/// characters and a disabled button on a full gallery are conveniences; the request
/// is assumed forged, so the caption is truncated and the count is counted here.
/// </summary>
public sealed class GalleryService(RmvDbContext db, ILogger<GalleryService> log)
{
    /// <summary>
    /// Stores an upload after deciding for itself what the bytes are.
    ///
    /// The stream is read to a capped buffer first, because a length header is a
    /// claim and not a promise. Only then is the format read out of the bytes, and
    /// only from ImageProbe's four. Nothing the caller said about the file is used.
    /// </summary>
    public async Task<UploadOutcome> AddAsync(
        Member member, Stream content, long declaredLength, string? caption, int? gameId, CancellationToken ct)
    {
        if (declaredLength > ImageProbe.MaxBytes)
        {
            return UploadOutcome.Fail(TooBig(declaredLength));
        }

        var count = await db.Screenshots.CountAsync(s => s.MemberId == member.Id, ct);
        if (count >= GalleryLimits.MaxPerMember)
        {
            return UploadOutcome.Fail(
                $"You have {count} screenshots up, which is the limit. Remove one first.");
        }

        byte[] bytes;

        try
        {
            bytes = await ReadCappedAsync(content, ct);
        }
        catch (TooLargeException)
        {
            return UploadOutcome.Fail(TooBig(ImageProbe.MaxBytes + 1));
        }

        if (bytes.Length == 0)
        {
            return UploadOutcome.Fail("That file was empty.");
        }

        if (ImageProbe.Probe(bytes) is not { } probed)
        {
            return UploadOutcome.Fail(
                "That is not a PNG, JPEG, GIF or WebP. The name and the type it "
                + "claims are ignored; what matters is what is inside it.");
        }

        // Null when the game was not chosen or no longer exists. A screenshot does
        // not need one.
        if (gameId is { } id && !await db.GamePresences.AnyAsync(g => g.Id == id, ct))
        {
            gameId = null;
        }

        var shot = new Screenshot
        {
            MemberId = member.Id,
            GamePresenceId = gameId,
            Caption = Tidy(caption),
            ContentType = probed.ContentType,
            Width = probed.Width,
            Height = probed.Height,
            Bytes = bytes.Length,
            UploadedAt = DateTimeOffset.UtcNow,
            Image = new ScreenshotImage { Bytes = bytes },
        };

        db.Screenshots.Add(shot);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            log.LogWarning(ex, "Could not store a screenshot for member {Member}.", member.Id);
            return UploadOutcome.Fail("Could not store that. Try again.");
        }

        return new UploadOutcome(true, shot, null);
    }

    /// <summary>
    /// Removes one, if this member may.
    ///
    /// Their own, or anything when they administer. Scoped in the query rather than
    /// checked after loading, so an id belonging to someone else is not found at all
    /// and there is no row in hand to accidentally act on.
    /// </summary>
    public async Task<bool> RemoveAsync(Member member, int id, CancellationToken ct)
    {
        var shot = await db.Screenshots
            .Where(s => s.Id == id && (member.CanAdminister || s.MemberId == member.Id))
            .FirstOrDefaultAsync(ct);

        if (shot is null)
        {
            return false;
        }

        // The image row goes by cascade rather than by remembering to delete it.
        db.Screenshots.Remove(shot);
        await db.SaveChangesAsync(ct);

        return true;
    }

    /// <summary>Plain text, trimmed, and truncated rather than rejected.</summary>
    private static string Tidy(string? caption)
    {
        var text = (caption ?? "").Trim();

        return text.Length <= GalleryLimits.MaxCaption
            ? text
            : text[..GalleryLimits.MaxCaption];
    }

    private static string TooBig(long length) =>
        $"That is {length / 1024 / 1024}MB. The limit is {ImageProbe.MaxBytes / 1024 / 1024}MB.";

    private sealed class TooLargeException : Exception;

    /// <summary>
    /// Reads to a hard cap, checked as it goes.
    ///
    /// A declared length is checked first because it avoids starting a pointless
    /// read, but it is not trusted: this is the check that actually holds.
    /// </summary>
    private static async Task<byte[]> ReadCappedAsync(Stream content, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var into = new MemoryStream();
        var total = 0;
        int read;

        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > ImageProbe.MaxBytes)
            {
                throw new TooLargeException();
            }

            into.Write(buffer, 0, read);
        }

        return into.ToArray();
    }
}

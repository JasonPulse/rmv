using System.Buffers.Binary;

namespace Rmv.Web.Gallery;

/// <summary>What the bytes actually are.</summary>
/// <param name="ContentType">From the allowlist below, never from the caller.</param>
public sealed record ProbedImage(string ContentType, int Width, int Height);

/// <summary>
/// Identifies an uploaded image from its own bytes.
///
/// Nothing the caller says about a file is trusted: not the name, not the
/// extension, not the declared content type. All three are attacker-controlled on
/// an upload, and the content type is the one that matters because the gallery
/// endpoint echoes it. A file that announces image/png and contains HTML would be
/// stored cross-site scripting served from our own origin.
///
/// So the format is read out of the header, and only these four are accepted:
///
///   PNG, JPEG, GIF, WebP
///
/// SVG is excluded deliberately even though browsers render it. It is a document
/// that can carry script, so serving one from our origin is the same hole by a
/// different route.
///
/// Dimensions come out of the same headers. They are not a security control, they
/// are what lets the grid reserve the right box for a picture before it loads, so
/// the page does not jump about as a row of screenshots arrives.
/// </summary>
public static class ImageProbe
{
    /// <summary>
    /// A screenshot, not a photo library. 4K PNG lands around 8MB and that is the
    /// largest thing anyone should be posting here.
    /// </summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Enough for a 5K ultrawide with room to spare. Past this the file is either a
    /// mistake or an attempt to make something downstream allocate a lot.
    /// </summary>
    public const int MaxDimension = 12000;

    public static ProbedImage? Probe(ReadOnlySpan<byte> bytes) =>
        Png(bytes) ?? Gif(bytes) ?? Jpeg(bytes) ?? WebP(bytes);

    /// <summary>
    /// 8-byte signature, then an IHDR chunk whose first two fields are the size.
    /// The signature is checked in full: the first byte alone is a common enough
    /// prefix that a truncated check would pass on unrelated files.
    /// </summary>
    private static ProbedImage? Png(ReadOnlySpan<byte> b)
    {
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

        if (b.Length < 24 || !b[..8].SequenceEqual(signature) || !b.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return null;
        }

        return Sized(
            "image/png",
            (int)BinaryPrimitives.ReadUInt32BigEndian(b.Slice(16, 4)),
            (int)BinaryPrimitives.ReadUInt32BigEndian(b.Slice(20, 4)));
    }

    /// <summary>GIF87a or GIF89a, then the logical screen size, little endian.</summary>
    private static ProbedImage? Gif(ReadOnlySpan<byte> b)
    {
        if (b.Length < 10 || (!b[..6].SequenceEqual("GIF87a"u8) && !b[..6].SequenceEqual("GIF89a"u8)))
        {
            return null;
        }

        return Sized(
            "image/gif",
            BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(6, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(8, 2)));
    }

    /// <summary>
    /// SOI, then a walk through the segment headers looking for a start-of-frame.
    ///
    /// The size is not at a fixed offset in a JPEG: it lives in whichever SOFn
    /// marker the encoder used, after any number of application and comment
    /// segments of arbitrary length. So the segments are walked rather than
    /// guessed at, and a malformed length that would run off the end stops the walk
    /// instead of reading past it.
    /// </summary>
    private static ProbedImage? Jpeg(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8)
        {
            return null;
        }

        var i = 2;

        while (i + 3 < b.Length)
        {
            if (b[i] != 0xFF)
            {
                // Fill bytes are legal between segments. Anything else means this is
                // not a shape worth reading further.
                i++;
                continue;
            }

            var marker = b[i + 1];
            i += 2;

            // Markers that carry no payload.
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }

            // Start of scan: the entropy-coded data begins, so there is no header
            // left to find.
            if (marker == 0xDA || marker == 0xD9)
            {
                return null;
            }

            if (i + 1 >= b.Length)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i, 2));
            if (length < 2 || i + length > b.Length)
            {
                return null;
            }

            // SOF0 through SOF15, skipping the four that are not frame headers.
            var isFrameHeader = marker is >= 0xC0 and <= 0xCF
                                && marker is not (0xC4 or 0xC8 or 0xCC);

            if (isFrameHeader)
            {
                // precision, height, width.
                if (length < 7)
                {
                    return null;
                }

                return Sized(
                    "image/jpeg",
                    BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 5, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 3, 2)));
            }

            i += length;
        }

        return null;
    }

    /// <summary>
    /// RIFF container, then one of three chunk layouts. VP8X carries the size as
    /// two 24-bit values minus one; VP8 and VP8L each hide it somewhere else again.
    /// </summary>
    private static ProbedImage? WebP(ReadOnlySpan<byte> b)
    {
        if (b.Length < 30 || !b[..4].SequenceEqual("RIFF"u8) || !b.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return null;
        }

        var chunk = b.Slice(12, 4);

        if (chunk.SequenceEqual("VP8X"u8))
        {
            var w = (b[24] | (b[25] << 8) | (b[26] << 16)) + 1;
            var h = (b[27] | (b[28] << 8) | (b[29] << 16)) + 1;
            return Sized("image/webp", w, h);
        }

        if (chunk.SequenceEqual("VP8 "u8))
        {
            // Keyframe header: a 3-byte start code, then 14 bits of each dimension.
            if (b.Length < 30 || b[23] != 0x9D || b[24] != 0x01 || b[25] != 0x2A)
            {
                return null;
            }

            var w = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(26, 2)) & 0x3FFF;
            var h = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(28, 2)) & 0x3FFF;
            return Sized("image/webp", w, h);
        }

        if (chunk.SequenceEqual("VP8L"u8))
        {
            if (b.Length < 25 || b[20] != 0x2F)
            {
                return null;
            }

            var bits = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(21, 4));
            return Sized(
                "image/webp",
                (int)(bits & 0x3FFF) + 1,
                (int)((bits >> 14) & 0x3FFF) + 1);
        }

        return null;
    }

    /// <summary>
    /// A format with an impossible size is not a picture we will serve. Zero is a
    /// header that was read wrong, and past MaxDimension is either a mistake or an
    /// attempt to make something allocate.
    /// </summary>
    private static ProbedImage? Sized(string contentType, int width, int height) =>
        width is > 0 and <= MaxDimension && height is > 0 and <= MaxDimension
            ? new ProbedImage(contentType, width, height)
            : null;
}

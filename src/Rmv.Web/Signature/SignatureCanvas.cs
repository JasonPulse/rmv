using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Rmv.Web.Signature;

/// <summary>What came of an upload.</summary>
public sealed record FittedImage(byte[] Bytes, int Width, int Height);

/// <summary>
/// Makes an uploaded picture into a background.
///
/// This is the answer to the storage worry, and it is worth being exact about why:
/// what gets stored is bounded by the canvas rather than by what somebody had on
/// their desktop. A 4000x3000 phone photo and a 520x160 gradient both come out of
/// here as a 520x160 PNG, so two backgrounds per member is a known number of
/// kilobytes rather than a hope. The 2014 version accepted two gigabytes and kept
/// whatever arrived.
///
/// Cropped to fill rather than stretched. A signature background is scenery, and a
/// squashed screenshot looks like a mistake the site made.
/// </summary>
public static class SignatureCanvas
{
    private static readonly PngEncoder Encoder = new()
    {
        CompressionLevel = PngCompressionLevel.Level6,
        // No alpha: this is scenery behind text, and the canvas underneath it is
        // opaque anyway. Dropping the channel is a quarter off the stored bytes.
        ColorType = PngColorType.Rgb,
    };

    /// <summary>
    /// The picture at canvas size, or null if it will not decode.
    ///
    /// Never throws on content. The bytes came from an upload, and ImageSharp is the
    /// thing that decides whether they are an image; a member choosing the wrong file
    /// gets told so rather than seeing a 500.
    /// </summary>
    public static FittedImage? Fit(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        try
        {
            using var image = Image.Load<Rgba32>(bytes);

            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(SignatureLimits.Width, SignatureLimits.Height),
                // Fill the canvas and crop the overflow, centred, so nothing is
                // distorted and nothing is letterboxed.
                Mode = ResizeMode.Crop,
                Position = AnchorPositionMode.Center,
            }));

            using var png = new MemoryStream();
            image.Save(png, Encoder);

            return new FittedImage(png.ToArray(), image.Width, image.Height);
        }
        catch (Exception ex) when (ex is ImageFormatException
                                      or NotSupportedException
                                      or InvalidImageContentException
                                      or OutOfMemoryException)
        {
            return null;
        }
    }
}

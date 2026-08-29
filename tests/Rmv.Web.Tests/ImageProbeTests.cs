using Rmv.Web.Gallery;

namespace Rmv.Web.Tests;

/// <summary>
/// What an upload actually is, read from its own bytes.
///
/// The fixtures are real files in all four formats, made by the encoders on a
/// machine rather than hand-assembled. Hand-written headers would only prove the
/// probe agrees with my reading of the specifications, which is the thing most
/// likely to be wrong: `file` reports the same dimensions these tests assert.
/// </summary>
public class ImageProbeTests
{
    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "images", name));

    [Theory]
    [InlineData("shot.png", "image/png", 1920, 1080)]
    [InlineData("shot.jpg", "image/jpeg", 1920, 1080)]
    [InlineData("shot.gif", "image/gif", 640, 480)]
    [InlineData("shot.webp", "image/webp", 1920, 1080)]
    public void Reads_the_format_and_size_of_a_real_file(
        string file, string type, int width, int height)
    {
        var probed = ImageProbe.Probe(Fixture(file));

        Assert.NotNull(probed);
        Assert.Equal(type, probed.ContentType);
        Assert.Equal(width, probed.Width);
        Assert.Equal(height, probed.Height);
    }

    [Fact]
    public void The_declared_type_and_the_name_are_never_consulted()
    {
        // The whole point. A caller can name a file anything and claim any content
        // type; only the bytes decide, and here the bytes are a PNG.
        var probed = ImageProbe.Probe(Fixture("shot.png"));

        Assert.Equal("image/png", probed!.ContentType);
    }

    [Theory]
    // HTML pretending to be an image. This is the one that matters: the gallery
    // endpoint echoes the stored content type, so accepting this would be stored
    // cross-site scripting served from our own origin.
    [InlineData("<!DOCTYPE html><script>alert(1)</script>")]
    [InlineData("<html><body>hello</body></html>")]
    // SVG renders in a browser and can carry script, so it is refused even though
    // it is genuinely an image format.
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    // Text, a shell script, and something that starts like a PNG and is not.
    [InlineData("just some text")]
    [InlineData("#!/bin/sh\nrm -rf /\n")]
    [InlineData("\x89PNGnope")]
    public void Anything_that_is_not_one_of_the_four_formats_is_refused(string content)
    {
        Assert.Null(ImageProbe.Probe(System.Text.Encoding.UTF8.GetBytes(content)));
    }

    [Fact]
    public void An_empty_upload_is_refused()
    {
        Assert.Null(ImageProbe.Probe([]));
        Assert.Null(ImageProbe.Probe(new byte[4]));
    }

    [Fact]
    public void A_truncated_header_is_refused_rather_than_read_past()
    {
        // Every length a file could be cut off at. None may throw, and none may
        // report a size the file does not contain.
        var png = Fixture("shot.png");

        for (var length = 0; length < 40; length++)
        {
            var probed = ImageProbe.Probe(png.AsSpan(0, length));

            if (probed is not null)
            {
                // If it does identify one, the header was complete by then.
                Assert.True(length >= 24, $"claimed a size from {length} bytes");
                Assert.Equal(1920, probed.Width);
            }
        }
    }

    [Fact]
    public void A_jpeg_with_a_lying_segment_length_stops_rather_than_reading_past()
    {
        var jpeg = Fixture("shot.jpg");

        // Point the first segment's length beyond the end of the file.
        jpeg[4] = 0xFF;
        jpeg[5] = 0xFF;

        // Null or a real answer, but never an exception and never a size read out
        // of memory past the buffer.
        var probed = ImageProbe.Probe(jpeg);
        if (probed is not null)
        {
            Assert.InRange(probed.Width, 1, ImageProbe.MaxDimension);
        }
    }

    [Fact]
    public void A_header_claiming_an_absurd_size_is_refused()
    {
        var png = Fixture("shot.png");

        // 100000 x 100000 would be a 40GB bitmap if anything ever decoded it.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 100_000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), 100_000);

        Assert.Null(ImageProbe.Probe(png));
    }

    [Fact]
    public void A_header_claiming_zero_is_refused()
    {
        var png = Fixture("shot.png");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 0);

        Assert.Null(ImageProbe.Probe(png));
    }

    [Fact]
    public void A_png_signature_without_an_ihdr_is_refused()
    {
        var png = Fixture("shot.png");
        png[12] = (byte)'X';

        Assert.Null(ImageProbe.Probe(png));
    }

    [Fact]
    public void A_riff_file_that_is_not_webp_is_refused()
    {
        // A WAV is RIFF too. The second four bytes are what separate them.
        var bytes = new byte[64];
        "RIFF"u8.CopyTo(bytes);
        "WAVE"u8.CopyTo(bytes.AsSpan(8));

        Assert.Null(ImageProbe.Probe(bytes));
    }
}

namespace Rmv.Web.Data;

/// <summary>
/// A member's forum signature: the design, and the picture it was last rendered
/// into.
///
/// One per member. The old generators kept a design per browser cookie with no
/// owner at all, so a design belonged to whoever guessed its number, and a member
/// who cleared their cookies started again.
///
/// The design is JSON rather than a column per field. The two DAoC versions had
/// twenty-three columns for a fixed grid of twelve text slots and seven start
/// points, which is why they could not express a position; the Tera one packed
/// lists into three columns as "1~text;2~text;". A design is a document, and this
/// is the shape SignatureDesign already has.
/// </summary>
public class Signature
{
    /// <summary>
    /// Twelve characters of base32, which is 60 bits: enough that a slug cannot be
    /// found by trying, and short enough to read out over voice chat.
    /// </summary>
    public const int SlugLength = 12;

    public int Id { get; set; }

    public int MemberId { get; set; }

    public Member? Member { get; set; }

    /// <summary>
    /// What the public URL is keyed on.
    ///
    /// Opaque and random, not the member id and not their name. A forum post embeds
    /// this address for years, so it must not encode anything about the account, and
    /// it must stay the same when they rename themselves.
    /// </summary>
    public string Slug { get; set; } = "";

    /// <summary>Serialised SignatureDesign. Validated on the way in, never trusted on the way out.</summary>
    public string Design { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public SignatureImage? Image { get; set; }
}

/// <summary>
/// The rendered picture, kept so that serving one costs no rendering.
///
/// This is the answer to the thing that made the old ones expensive: sig.php
/// scraped the herald and ran GD on every request for the image, and sent
/// Cache-Control: no-cache while doing it. Ten signatures in a forum page meant ten
/// scrapes and ten renders. Here a request is one indexed read of these bytes.
///
/// Its own table for the same reason a portrait's bytes are: a query that wants to
/// know when a signature was last rendered should not drag a hundred kilobytes of
/// PNG along with it.
/// </summary>
public class SignatureImage
{
    public int SignatureId { get; set; }

    public Signature? Signature { get; set; }

    public byte[] Bytes { get; set; } = [];

    /// <summary>
    /// A digest of Bytes, which is what the ETag is built from.
    ///
    /// The picture is its own version here for the same reason a portrait is: it is
    /// the only thing that cannot be wrong about whether the picture changed.
    /// </summary>
    public string Version { get; set; } = "";

    /// <summary>
    /// A digest of everything that went into the render: the design, the text every
    /// element resolved to, and the background.
    ///
    /// This is what makes the daily pass cheap. Recomputing it is a few string
    /// hashes; rendering is not. A pass that finds it unchanged does nothing at all,
    /// so a member whose stats did not move keeps the bytes a browser already has.
    /// </summary>
    public string SourceVersion { get; set; } = "";

    public DateTimeOffset RenderedAt { get; set; }
}

/// <summary>
/// A background a member uploaded, at most SignatureLimits.MaxBackgrounds of them.
///
/// Re-encoded to the canvas before it is stored, so what is kept is bounded by the
/// canvas rather than by what somebody had on their desktop. The old one accepted
/// two gigabytes.
/// </summary>
public class SignatureBackground
{
    public int Id { get; set; }

    public int MemberId { get; set; }

    public Member? Member { get; set; }

    public byte[] Bytes { get; set; } = [];

    /// <summary>Always image/png after re-encoding, but stored rather than assumed.</summary>
    public string ContentType { get; set; } = "";

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>What the member called it, so the picker can label two of them.</summary>
    public string Name { get; set; } = "";

    public DateTimeOffset UploadedAt { get; set; }
}

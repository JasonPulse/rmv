namespace Rmv.Web.Data;

/// <summary>
/// One screenshot in the gallery, without its bytes.
///
/// Twenty years of DAoC and FFXI screenshots is the content this guild actually
/// has, and it is the thing Discord is worst at keeping: a channel scrolls, an
/// attachment expires behind a login, and nobody can find the good one from 2004.
/// </summary>
public class Screenshot
{
    public int Id { get; set; }

    public int MemberId { get; set; }

    public Member? Member { get; set; }

    /// <summary>Optional. A screenshot does not have to belong to a game we list.</summary>
    public int? GamePresenceId { get; set; }

    public GamePresence? Game { get; set; }

    /// <summary>What it is. Plain text; the gallery never renders it as markup.</summary>
    public string Caption { get; set; } = "";

    /// <summary>From ImageProbe's allowlist, never from what the upload claimed.</summary>
    public string ContentType { get; set; } = "";

    /// <summary>Read from the file header, so the grid can reserve the right box.</summary>
    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>Kept so a listing can show it without loading the image.</summary>
    public int Bytes { get; set; }

    public DateTimeOffset UploadedAt { get; set; }

    public ScreenshotImage? Image { get; set; }

    /// <summary>Where a page points an img. See ScreenshotEndpoint.</summary>
    public string Path => ScreenshotEndpoint.PathFor(Id);
}

/// <summary>
/// The bytes of one screenshot.
///
/// Its own table, one row per screenshot, for the same reason character portraits
/// have one: a bytea column would be loaded by every query that lists the gallery,
/// and a page of twenty screenshots would pull twenty full images out of Postgres
/// to render twenty captions.
/// </summary>
public class ScreenshotImage
{
    public int ScreenshotId { get; set; }

    public Screenshot? Screenshot { get; set; }

    public byte[] Bytes { get; set; } = [];
}

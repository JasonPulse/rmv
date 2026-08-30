namespace Rmv.Web.Data;

/// <summary>
/// The bytes of one character's portrait.
///
/// Its own table, one row per character, rather than a column on characters. A
/// bytea column would be loaded by every query that touches a character, and
/// /history reads every character on the site to build the game cards. 120KB per
/// row times every card is a page nobody wants to wait for.
/// </summary>
public class CharacterPortrait
{
    /// <summary>Primary key and foreign key: one portrait per character.</summary>
    public int CharacterId { get; set; }

    public Character? Character { get; set; }

    public byte[] Bytes { get; set; } = [];

    /// <summary>As the herald served it. Sent straight back out, from an allowlist.</summary>
    public string ContentType { get; set; } = "image/png";

    /// <summary>
    /// A digest of Bytes, matching Character.PortraitVersion. Kept here as well so
    /// the endpoint can build an ETag without a second read, and so a half-finished
    /// refresh is detectable rather than silent.
    /// </summary>
    public string Version { get; set; } = "";

    public DateTimeOffset FetchedAt { get; set; }
}

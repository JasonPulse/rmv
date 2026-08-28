namespace Rmv.Web.Data;

/// <summary>
/// A spellcraft item a member saved to come back to, owned by that member.
///
/// The design is stored as one encoded string rather than as a row per socket.
/// SpellcraftDesign owns that format, and it is version-marked, so a change to
/// the shape of a design is caught on read rather than silently misparsed.
///
/// Ordinal is what makes the five-template cap real. It is 1 to MaxPerMember and
/// unique per member in the database, so there is no arrangement of concurrent
/// forged requests that leaves a member with six rows. A handler-only count is a
/// check; a unique index is the rule.
/// </summary>
public class SpellcraftTemplate
{
    /// <summary>
    /// How many templates one member may keep. The number appears here and
    /// nowhere else: the database constraint, the store, the page and the view all
    /// read it from this constant.
    /// </summary>
    public const int MaxPerMember = 5;

    public const int MaxNameLength = 40;

    public int Id { get; set; }

    public int MemberId { get; set; }

    public Member? Member { get; set; }

    /// <summary>1 to MaxPerMember, unique within the member. See the class comment.</summary>
    public int Ordinal { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Whatever SpellcraftDesign.Encode wrote. Never parsed anywhere else.</summary>
    public string Design { get; set; } = "";

    public DateTimeOffset SavedAt { get; set; }
}

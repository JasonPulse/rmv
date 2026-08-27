using Rmv.Web.Data;

namespace Rmv.Web.Pages.Shared;

/// <summary>
/// What _CharacterBody renders. The card's own wrapper and footer stay with the
/// page, because the two pages that show characters need different footers: one
/// has refresh and remove, the other is public and has neither. Everything above
/// the footer is identical, and that is what lives in the partial.
/// </summary>
public sealed record CharacterCard(Character Character)
{
    /// <summary>Shown beside the name. The roster uses it to mark the one clicked.</summary>
    public string? Badge { get; init; }

    public string? BadgeClass { get; init; }

    /// <summary>
    /// The roster already groups by game under a heading, so repeating it on every
    /// card there would be noise. The member's own list is a flat list and needs it.
    /// </summary>
    public bool ShowGame { get; init; }

    /// <summary>DAoC only in practice, and only interesting to the owner.</summary>
    public bool ShowKills { get; init; }
}

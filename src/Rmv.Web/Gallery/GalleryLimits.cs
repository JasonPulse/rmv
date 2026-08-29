namespace Rmv.Web.Gallery;

/// <summary>
/// The numbers the gallery refuses past.
///
/// One place, because a limit written into a form and a limit enforced on the
/// server are the same limit and must not be two.
/// </summary>
public static class GalleryLimits
{
    public const int MaxCaption = 200;

    /// <summary>
    /// A runaway guard, not a product rule. Images live in Postgres, so an
    /// unbounded gallery is an unbounded database, and nine people with a decade of
    /// screenshots each is a number worth having a ceiling on. Raise it freely.
    /// </summary>
    public const int MaxPerMember = 60;

    /// <summary>How many appear on one page of the gallery.</summary>
    public const int PageSize = 24;
}

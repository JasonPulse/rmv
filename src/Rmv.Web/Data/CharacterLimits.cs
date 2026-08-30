namespace Rmv.Web.Data;

/// <summary>
/// The numbers a character sheet refuses past.
///
/// One place, for the same reason GalleryLimits is one place: a limit in a form
/// attribute, a limit in the service and a column width are the same limit, and
/// they were three. They agreed, but nothing made them agree. Raising the level cap
/// in the service without touching the attribute means the form rejects a value the
/// server would have taken; widening the attribute without the column means a
/// database error instead of a message.
/// </summary>
public static class CharacterLimits
{
    /// <summary>The stored name. No game the guild has played allows longer.</summary>
    public const int MaxName = 32;

    public const int MinName = 2;

    /// <summary>
    /// What the add form accepts, which is longer than a name.
    ///
    /// Some heralds are keyed by id and take a pasted character URL, so the typed
    /// value is not always a name. The adapter decides what its own server accepts;
    /// this is only a bound on what is worth sending.
    /// </summary>
    public const int MaxTyped = 200;

    /// <summary>Job or class, as typed for a game with no herald.</summary>
    public const int MaxClass = 60;

    public const int MinLevel = 1;

    /// <summary>
    /// Wide on purpose. DAoC stops at 50, FFXI at 99, FFXIV at 100, and the next
    /// server the guild lands on will pick its own number.
    /// </summary>
    public const int MaxLevel = 999;
}

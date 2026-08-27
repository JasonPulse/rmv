namespace Rmv.Web.Pages.Shared;

/// <summary>
/// What _Flash renders. A record rather than a base page model because the four
/// pages that report this have nothing else in common, and giving them a shared
/// base to share two nullable strings would be the more expensive mistake.
/// </summary>
public sealed record Flash(string? Notice, string? Error = null);

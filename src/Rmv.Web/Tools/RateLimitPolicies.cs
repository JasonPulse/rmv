namespace Rmv.Web.Tools;

public static class RateLimitPolicies
{
    /// <summary>
    /// Guards the unauthenticated upload endpoints. Parsing is cheap, but a file
    /// upload is the one place an anonymous visitor can make the server do work
    /// proportional to what they send.
    /// </summary>
    public const string Upload = "upload";

    /// <summary>
    /// Adding a character fetches from someone else's herald, so this is limited
    /// more tightly than an upload: the cost lands on a server that is not ours.
    /// </summary>
    public const string Herald = "herald";

    /// <summary>
    /// The signature image, which is the only route strangers hit in bulk: it is
    /// embedded in forum posts and loaded by everybody reading them.
    ///
    /// Generous, because the honest case is a busy thread and the answer is nearly
    /// free: stored bytes, or a 304. This exists for the dishonest case, which the
    /// 2014 version met by sniffing user agents for "bot".
    /// </summary>
    public const string Signature = "signature";
}

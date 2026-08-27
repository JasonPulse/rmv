using System.Security.Claims;

namespace Rmv.Web.Data;

/// <summary>
/// The signed-in member, resolved once per request.
///
/// Exists so the alias can take over everywhere the site names someone. The
/// alternative was putting it in a claim at sign-in, which would mean changing
/// your alias did nothing until you signed out and back in. One indexed lookup
/// per request, for signed-in callers only, is the cheaper mistake.
/// </summary>
public sealed class CurrentMember(MemberDirectory directory)
{
    private Member? _member;
    private bool _loaded;

    public async Task<Member?> GetAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (_loaded)
        {
            return _member;
        }

        _loaded = true;

        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        _member = await directory.EnsureAsync(user, ct);
        return _member;
    }

    /// <summary>
    /// The alias if set, else the Discord name. Never the Discord id.
    ///
    /// Swallows a database failure to get there, because naming someone in the
    /// masthead is not worth failing a page render over. The swallow lives here
    /// rather than in GetAsync deliberately: a handler deciding whether you own a
    /// character needs an outage to surface as an outage, not as "could not
    /// identify your account".
    /// </summary>
    public async Task<string> HandleAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        try
        {
            return (await GetAsync(user, ct))?.Handle ?? DiscordUser.Name(user);
        }
        catch
        {
            return DiscordUser.Name(user);
        }
    }

    /// <summary>
    /// Initials of the Handle, not of the Discord name. The masthead diamond and
    /// the name beside it have to agree, and they did not: the diamond read the
    /// claim, so an alias changed the name and left the diamond on the old one.
    /// </summary>
    public async Task<string> InitialsAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
        Member.InitialsOf(await HandleAsync(user, ct));
}

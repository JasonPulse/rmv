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

        try
        {
            _member = await directory.EnsureAsync(user, ct);
        }
        catch
        {
            // Naming someone is not worth failing a page render over.
            _member = null;
        }

        return _member;
    }

    /// <summary>The alias if set, else the Discord name. Never the Discord id.</summary>
    public async Task<string> HandleAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
        (await GetAsync(user, ct))?.Handle ?? DiscordUser.Name(user);
}

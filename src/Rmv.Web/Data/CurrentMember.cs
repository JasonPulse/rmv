using System.Security.Claims;

namespace Rmv.Web.Data;

/// <summary>
/// The signed-in member and what they may do, resolved once per request.
///
/// Two jobs, and they belong together. The member row is here so the alias can
/// take over everywhere the site names someone; putting it in a claim at sign-in
/// would mean changing your alias did nothing until you signed out and back in.
/// One indexed lookup per request, for signed-in callers only, is the cheaper
/// mistake.
///
/// <see cref="AccessAsync"/> is the only thing in the application that decides what
/// someone may do. The authorization handlers ask it, the masthead asks it, and
/// every page asks it. Nothing else folds configuration together with a member row,
/// because doing that in more than one place is what produced a root admin who
/// passed every policy while the site showed them no upload button. See
/// <see cref="Access"/>.
///
/// The member is registered even with no database configured, in which case
/// <paramref name="directory"/> is null and access is whatever configuration alone
/// allows. That is the same path an outage takes, so there is one branch rather
/// than a null check in every caller.
/// </summary>
public sealed class CurrentMember(
    IConfiguration config,
    ILogger<CurrentMember> log,
    MemberDirectory? directory = null)
{
    private Member? _member;
    private bool _loaded;
    private Access? _access;

    /// <summary>
    /// What this caller may do.
    ///
    /// Cached for the request, so authorization, the masthead and the page body
    /// cannot give three different answers within one render.
    ///
    /// A database failure is caught rather than thrown: access falls back to what
    /// configuration alone allows, which keeps a root admin working through an
    /// outage and denies everyone else. Failing closed is the rule for this
    /// question, and it has to be, since the alternative on an unreadable row is
    /// guessing in the caller's favour.
    /// </summary>
    public async Task<Access> AccessAsync(ClaimsPrincipal? user, CancellationToken ct = default)
    {
        if (_access is not null)
        {
            return _access;
        }

        var id = user is null ? null : DiscordUser.Id(user);
        if (user?.Identity?.IsAuthenticated != true || string.IsNullOrEmpty(id))
        {
            return _access = Access.None;
        }

        Member? member = null;

        try
        {
            member = await GetAsync(user, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "Could not read the member row for {Id}; access is what configuration allows.", id);
        }

        return _access = Access.Of(id, member, AdminPolicy.IsRootAdmin(config, id));
    }

    /// <summary>
    /// Their row, creating it if it is missing. Throws if the database cannot be
    /// reached, on purpose: a handler deciding whether you own a character needs an
    /// outage to surface as an outage rather than as "no such account".
    /// </summary>
    public async Task<Member?> GetAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (_loaded)
        {
            return _member;
        }

        if (user.Identity?.IsAuthenticated != true || directory is null)
        {
            _loaded = true;
            return null;
        }

        _member = await directory.EnsureAsync(user, ct);

        // Marked loaded after the await, not before. A failed read used to be
        // cached as "no account", so one database blip turned every later question
        // in the same request into a confident wrong answer.
        _loaded = true;

        return _member;
    }

    /// <summary>
    /// The alias if set, else the Discord name. Never the Discord id.
    ///
    /// Swallows a database failure to get there, because naming someone in the
    /// masthead is not worth failing a page render over.
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

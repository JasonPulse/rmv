using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Rmv.Web.Data;

public sealed class AdminRequirement : IAuthorizationRequirement;

/// <summary>An approved member. Admins satisfy it too.</summary>
public sealed class ApprovedMemberRequirement : IAuthorizationRequirement;

/// <summary>
/// Authorisation, as distinct from authentication.
///
/// Discord sign-in proves someone has a Discord account, which is not a
/// qualification for editing the site. Admin comes from one of two places:
///
///   1. Admin:DiscordIds in configuration. These are root admins. They cannot be
///      revoked from inside the app, and they still work when the database is
///      unreachable, which is what stops a bad grant or an outage locking you
///      out of your own site.
///   2. Member.IsAdmin in the database, granted by an existing admin at
///      /admin/members.
///
/// It fails closed: no config ids and no database admin means nobody is an
/// admin, rather than everybody.
/// </summary>
public static class AdminPolicy
{
    public const string Name = "Admin";

    /// <summary>Comma or whitespace separated Discord user ids, from Admin:DiscordIds.</summary>
    public static string[] Parse(string? configured) =>
        (configured ?? "")
            .Split([',', ' ', ';', '\n', '\r', '\t'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public static bool IsRootAdmin(IConfiguration config, string? discordId) =>
        discordId is not null
        && Parse(config["Admin:DiscordIds"]).Contains(discordId, StringComparer.Ordinal);
}

public static class MemberPolicy
{
    /// <summary>Approved by an admin. Required to add or claim a character.</summary>
    public const string Approved = "ApprovedMember";
}

/// <summary>
/// The shape both member policies share: read the Discord id off the principal,
/// let root admins through without touching the database, then look the member up
/// and ask the requirement's own question.
///
/// The two handlers were copies of each other differing only in that question,
/// and the copies had drifted: one expressed "blocked beats admin" as a SQL
/// predicate, the other as a pattern match, and they did not agree. Security code
/// is the worst place to keep two versions of a rule.
///
/// Fails closed throughout. No id, no database, or a database that throws all end
/// without calling Succeed, so the answer is no.
/// </summary>
public abstract class MemberRequirementHandler<TRequirement>(
    IServiceProvider services,
    IConfiguration config,
    ILogger log) : AuthorizationHandler<TRequirement>
    where TRequirement : IAuthorizationRequirement
{
    /// <summary>What this requirement asks of a member. Both answers live on Member.</summary>
    protected abstract bool Qualifies(Member member);

    /// <summary>For the log line, so a denial can be traced to a requirement.</summary>
    protected abstract string What { get; }

    protected sealed override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, TRequirement requirement)
    {
        var id = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        // Checked first and without touching the database, so a root admin can
        // always get in to fix things.
        if (AdminPolicy.IsRootAdmin(config, id))
        {
            context.Succeed(requirement);
            return;
        }

        var db = services.GetService<RmvDbContext>();
        if (db is null)
        {
            return;
        }

        try
        {
            // The whole row rather than a SQL predicate, so the rule can be one
            // property on Member instead of an expression tree per handler. One
            // indexed read on an authorised request.
            var member = await db.Members
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.DiscordId == id);

            if (member is not null && Qualifies(member))
            {
                context.Succeed(requirement);
            }
        }
        catch (Exception ex)
        {
            // Database down. Root admins already succeeded above; everyone else
            // is denied rather than allowed.
            log.LogWarning(ex, "Could not check {What} for {Id}.", What, id);
        }
    }
}

/// <summary>Approved members, and admins. Scoped so it can resolve the DbContext.</summary>
public sealed class ApprovedMemberAuthorizationHandler(
    IServiceProvider services,
    IConfiguration config,
    ILogger<ApprovedMemberAuthorizationHandler> log)
    : MemberRequirementHandler<ApprovedMemberRequirement>(services, config, log)
{
    protected override string What => "contributor status";

    protected override bool Qualifies(Member member) => member.CanContribute;
}

/// <summary>
/// Scoped, so it can resolve the scoped DbContext. Registered even when no
/// database exists, in which case only config admins pass.
/// </summary>
public sealed class AdminAuthorizationHandler(
    IServiceProvider services,
    IConfiguration config,
    ILogger<AdminAuthorizationHandler> log)
    : MemberRequirementHandler<AdminRequirement>(services, config, log)
{
    protected override string What => "admin status";

    protected override bool Qualifies(Member member) => member.CanAdminister;
}

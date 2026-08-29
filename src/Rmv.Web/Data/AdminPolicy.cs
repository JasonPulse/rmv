using Microsoft.AspNetCore.Authorization;

namespace Rmv.Web.Data;

public sealed class AdminRequirement : IAuthorizationRequirement;

/// <summary>An approved member. Admins satisfy it too.</summary>
public sealed class ApprovedMemberRequirement : IAuthorizationRequirement;

/// <summary>
/// Where the root ids are read, and nothing else.
///
/// Discord sign-in proves someone has a Discord account, which is not a
/// qualification for editing the site. What the ids mean, and how they combine with
/// the member row, is <see cref="Access"/>. This is only the parsing.
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

    /// <summary>
    /// Named in Admin:DiscordIds. Not an access decision by itself: it is one of the
    /// two inputs <see cref="Access.Of"/> folds. A caller reaching for this to decide
    /// something is the mistake this file used to encourage.
    /// </summary>
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
/// The shape both member policies share: get the one access answer, then ask the
/// requirement's own question of it.
///
/// This used to resolve the DbContext and work out the answer here. That made the
/// policies a second implementation of a question the rest of the site was also
/// answering off the member row, and the two disagreed for exactly the person who
/// most needed them not to. Now the handler decides nothing. If this class and a
/// page ever disagree again, one of them is not calling AccessAsync.
///
/// Fails closed: <see cref="CurrentMember.AccessAsync"/> answers no for an
/// anonymous caller, no database, or a database that throws.
/// </summary>
public abstract class MemberRequirementHandler<TRequirement>(CurrentMember me)
    : AuthorizationHandler<TRequirement>
    where TRequirement : IAuthorizationRequirement
{
    /// <summary>Which of the two answers this requirement wants.</summary>
    protected abstract bool Qualifies(Access access);

    protected sealed override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, TRequirement requirement)
    {
        if (Qualifies(await me.AccessAsync(context.User)))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>Approved members, and admins. Scoped, because the answer is per request.</summary>
public sealed class ApprovedMemberAuthorizationHandler(CurrentMember me)
    : MemberRequirementHandler<ApprovedMemberRequirement>(me)
{
    protected override bool Qualifies(Access access) => access.CanContribute;
}

/// <summary>Admins, from configuration or from the member row.</summary>
public sealed class AdminAuthorizationHandler(CurrentMember me)
    : MemberRequirementHandler<AdminRequirement>(me)
{
    protected override bool Qualifies(Access access) => access.CanAdminister;
}

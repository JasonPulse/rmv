using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rmv.Web.Data;
using Rmv.Web.Pages.Tools.Daoc;
using Rmv.Web.Tools.Spellcraft;

namespace Rmv.Web.Tests;

/// <summary>
/// The page's own gate, driven by hand with no browser and no form.
///
/// The store tests prove the cap and the scoping. These prove the page reaches
/// them the way it claims to: that saving asks for the approved policy rather
/// than for a cookie, and that a signed-in stranger gets nothing. Razor Pages
/// ignores [Authorize] on a handler method, so that check is written out in the
/// handler and is exactly the sort of thing that quietly stops working.
///
/// The authorisation service here is the real one with the real policy and the
/// real handler, not a stub that answers yes. A stub would pass whatever the page
/// did.
/// </summary>
public class SpellcraftPageTests : SpellcraftDatabaseTests
{
    private ServiceProvider _services = null!;

    /// <summary>
    /// The site's own registrations, minus the ones a page never touches. The
    /// DbContext is the fixture's, so the tests can look at what the page wrote.
    /// </summary>
    private ServiceProvider Services()
    {
        if (_services is not null)
        {
            return _services;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(Db);
        services.AddScoped<MemberDirectory>();
        services.AddScoped<CurrentMember>();
        services.AddSingleton(Store);
        services.AddScoped<IAuthorizationHandler, ApprovedMemberAuthorizationHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(MemberPolicy.Approved, p => p
                .RequireAuthenticatedUser()
                .AddRequirements(new ApprovedMemberRequirement()));

        return _services = services.BuildServiceProvider();
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static ClaimsPrincipal SignedInAs(Member member) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, member.DiscordId)],
            authenticationType: "Test"));

    private SpellcraftModel PageFor(ClaimsPrincipal user)
    {
        // A scope per page, because a request is a scope. CurrentMember caches the
        // access answer for its lifetime, so sharing one across pages would let one
        // test's answer stand in for the next one's.
        var services = Services().CreateScope().ServiceProvider;
        var http = new DefaultHttpContext { User = user, RequestServices = services };

        return new SpellcraftModel(Tables, services)
        {
            PageContext = new PageContext(new ActionContext(
                http, new RouteData(), new PageActionDescriptor(), new ModelStateDictionary())),
        };
    }

    /// <summary>Fills in the form the way a browser would, then posts it.</summary>
    private async Task<IActionResult> PostSaveAsync(
        SpellcraftModel page, string name, int? overwriteId = null)
    {
        page.Design = SpellcraftModel.DesignInput.From(Design("chest", "str-1"));
        page.Save = new SpellcraftModel.SaveInput { Name = name, OverwriteId = overwriteId };

        return await page.OnPostSaveAsync(default);
    }

    // --- who may save --------------------------------------------------------

    [Fact]
    public async Task An_anonymous_visitor_is_sent_to_sign_in_and_saves_nothing()
    {
        var result = await PostSaveAsync(PageFor(Anonymous()), "Forged");

        Assert.IsType<ChallengeResult>(result);
        Assert.Equal(0, await Db.SpellcraftTemplates.CountAsync());
    }

    [Fact]
    public async Task A_signed_in_member_who_is_not_approved_is_refused()
    {
        // The whole reason the handler asks the policy rather than the cookie.
        var result = await PostSaveAsync(PageFor(SignedInAs(Pending)), "Not yet");

        Assert.IsType<ForbidResult>(result);
        Assert.Equal(0, await CountAsync(Pending.Id));
    }

    [Fact]
    public async Task An_approved_member_saves()
    {
        var result = await PostSaveAsync(PageFor(SignedInAs(Member)), "Mine");

        Assert.IsType<RedirectToPageResult>(result);
        var saved = Assert.Single(await Store.ListAsync(Member.Id, default));
        Assert.Equal("Mine", saved.Name);
    }

    [Fact]
    public async Task An_unapproved_member_cannot_delete_either()
    {
        var theirs = await Store.SaveAsync(Other.Id, "Theirs", Design(), null, default);
        Assert.True(theirs.Ok, theirs.Error);

        var result = await PageFor(SignedInAs(Pending)).OnPostDeleteAsync(theirs.Template!.Id, default);

        Assert.IsType<ForbidResult>(result);
        Assert.Equal(1, await CountAsync(Other.Id));
    }

    // --- the cap, through the page -------------------------------------------

    [Fact]
    public async Task The_page_refuses_a_sixth_and_offers_the_overwrite_instead()
    {
        await FillToCapAsync(Member.Id);

        var page = PageFor(SignedInAs(Member));
        var result = await PostSaveAsync(page, "Sneaky sixth");

        // Re-rendered rather than redirected, carrying the reason and the prompt.
        Assert.IsType<PageResult>(result);
        Assert.True(page.MustOverwrite);
        Assert.Contains(SpellcraftTemplate.MaxPerMember.ToString(), page.Error!);
        Assert.Equal(SpellcraftTemplate.MaxPerMember, await CountAsync(Member.Id));
    }

    [Fact]
    public async Task Overwriting_through_the_page_keeps_the_count_at_the_cap()
    {
        await FillToCapAsync(Member.Id);
        var target = (await Store.ListAsync(Member.Id, default))[2];

        var result = await PostSaveAsync(PageFor(SignedInAs(Member)), "Replaced", target.Id);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(SpellcraftTemplate.MaxPerMember, await CountAsync(Member.Id));
        Assert.Equal("Replaced", (await Store.FindAsync(Member.Id, target.Id, default))!.Name);
    }

    [Fact]
    public async Task Overwriting_somebody_elses_template_through_the_page_is_refused()
    {
        var theirs = await Store.SaveAsync(Other.Id, "Theirs", Design(), null, default);
        Assert.True(theirs.Ok, theirs.Error);

        var page = PageFor(SignedInAs(Member));
        var result = await PostSaveAsync(page, "Mine now", theirs.Template!.Id);

        Assert.IsType<PageResult>(result);
        Assert.Contains("not one of yours", page.Error!);
        Assert.Equal("Theirs", (await Store.FindAsync(Other.Id, theirs.Template.Id, default))!.Name);
        Assert.Equal(0, await CountAsync(Member.Id));
    }

    // --- loading -------------------------------------------------------------

    [Fact]
    public async Task Loading_somebody_elses_template_by_id_shows_an_empty_form()
    {
        var theirs = await Store.SaveAsync(
            Other.Id, "Theirs", Design("helm", "dex-2"), null, default);
        Assert.True(theirs.Ok, theirs.Error);

        var page = PageFor(SignedInAs(Member));
        await page.OnGetAsync(theirs.Template!.Id, default);

        // Not found rather than refused, so the page cannot be used to discover
        // that the id exists at all.
        Assert.NotEqual("helm", page.Design.Slot);
        Assert.Empty(page.Save.Name);
    }

    [Fact]
    public async Task Loading_your_own_template_fills_the_form_back_in()
    {
        var mine = await Store.SaveAsync(
            Member.Id, "Resist helm", Design("helm", "dex-2", "", "body-3"), null, default);
        Assert.True(mine.Ok, mine.Error);

        var page = PageFor(SignedInAs(Member));
        await page.OnGetAsync(mine.Template!.Id, default);

        Assert.Equal("helm", page.Design.Slot);
        Assert.Equal(51, page.Design.Level);
        Assert.Equal("Resist helm", page.Save.Name);
        Assert.Equal(mine.Template.Id, page.Save.OverwriteId);
        // The helm has three sockets, so the fourth code is dropped on the way in.
        Assert.Equal(["dex-2", "", "body-3"], page.Design.Gems);
        Assert.NotNull(page.Report);
    }

    // --- the page's own gate --------------------------------------------------

    [Fact]
    public void The_whole_page_is_approved_members_only()
    {
        // The requirement is that no part of the calculator is public, not just
        // that saving is guarded. Calling a handler in a test bypasses the
        // attribute entirely, so a handler-level test cannot see this and an
        // earlier test asserted the opposite while still passing.
        var attribute = typeof(SpellcraftModel)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        Assert.NotNull(attribute);
        Assert.Equal(MemberPolicy.Approved, attribute.Policy);
    }

    [Fact]
    public async Task The_arithmetic_itself_asks_nobody_for_permission()
    {
        // Not a statement about who may reach the page: the attribute above
        // settles that. This is that the calculation does not consult the member
        // at all, which is what keeps it a pure function with the account
        // questions on the outside.
        var page = PageFor(Anonymous());
        page.Design = SpellcraftModel.DesignInput.From(Design("chest", "str-3", "str-3"));

        await page.OnPostCalculateAsync(default);

        Assert.NotNull(page.Report);
        Assert.False(page.CanSave);
        Assert.Empty(page.Templates);
        Assert.Equal(44, page.Report!.Bonuses.Single().Total);
    }

    protected override async ValueTask DisposeExtraAsync()
    {
        await base.DisposeExtraAsync();

        if (_services is not null)
        {
            await _services.DisposeAsync();
        }
    }
}

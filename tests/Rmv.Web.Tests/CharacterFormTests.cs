using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Rmv.Web.Data;
using Rmv.Web.Herald;
using Rmv.Web.Pages.Characters;

namespace Rmv.Web.Tests;

/// <summary>
/// What the add form offers, per game, driven by hand with no browser.
///
/// Three states, and the page decides none of them: which fields a game needs, and
/// whether the lookup can be skipped, both come from the registered adapter. This
/// is the check that the form cannot offer a choice the server then refuses, which
/// is the way a checkbox like this usually goes wrong.
/// </summary>
public class CharacterFormTests : HeraldDatabaseTests
{
    private int _armoryGameId;

    protected override void ConfigureHerald(FakeHeraldAdapter herald) =>
        herald.WithCharacter("Enchantress");

    protected override async Task SeedAsync()
    {
        // A game wired to the real Armory adapter, which is the one herald that
        // admits it does not list every character.
        var game = new GamePresence
        {
            Game = $"WoW {Guid.NewGuid():N}"[..20],
            Guilds = "RMV",
            HeraldAdapterKey = new WowArmoryAdapter(Fetcher).Key,
            IsActive = true,
        };

        Db.GamePresences.Add(game);
        await Db.SaveChangesAsync();
        _armoryGameId = game.Id;
        Registered.Add(game.Id);
    }

    /// <summary>Extra games this class made, so the base class clears them.</summary>
    private List<int> Registered { get; } = [];

    protected override async ValueTask DisposeExtraAsync()
    {
        Db.GamePresences.RemoveRange(Db.GamePresences.Where(g => Registered.Contains(g.Id)));
        await Db.SaveChangesAsync();
    }

    private IndexModel Page()
    {
        var registry = new HeraldRegistry([Herald, new WowArmoryAdapter(Fetcher)]);
        var config = new ConfigurationBuilder().Build();

        // No SignatureService in this provider on purpose: the page redraws a
        // signature after a change and must work where signatures are not
        // registered, which is the site running without a database.
        var services = new ServiceCollection().BuildServiceProvider();

        var model = new IndexModel(
            Db,
            new CharacterService(Db, registry, Fetcher, NullLogger<CharacterService>.Instance),
            registry,
            new CurrentMember(config, NullLogger<CurrentMember>.Instance,
                new MemberDirectory(Db, config, NullLogger<MemberDirectory>.Instance)),
            services,
            NullLogger<IndexModel>.Instance);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Member.DiscordId)], "TestAuth")),
        };

        model.PageContext = new PageContext(new ActionContext(
            http, new RouteData(), new PageActionDescriptor(), new ModelStateDictionary()));

        return model;
    }

    [Fact]
    public async Task The_armory_game_offers_the_choice_and_says_why()
    {
        var page = Page();
        await page.OnGetAsync(default);

        Assert.Contains(_armoryGameId, page.HeraldGameIds);

        var note = page.HeraldNotes[_armoryGameId];
        Assert.Contains("subscription", note, StringComparison.OrdinalIgnoreCase);

        // The same words the adapter uses, not a copy written into the page.
        Assert.Equal(((IHeraldAdapter)new WowArmoryAdapter(Fetcher)).CoverageNote, note);
    }

    [Fact]
    public async Task A_herald_that_lists_everyone_offers_no_choice()
    {
        var page = Page();
        await page.OnGetAsync(default);

        Assert.Contains(HeraldGameId, page.HeraldGameIds);
        Assert.False(page.HeraldNotes.ContainsKey(HeraldGameId));
    }

    [Fact]
    public async Task A_game_with_no_herald_offers_neither()
    {
        var page = Page();
        await page.OnGetAsync(default);

        Assert.DoesNotContain(NoHeraldGameId, page.HeraldGameIds);
        Assert.False(page.HeraldNotes.ContainsKey(NoHeraldGameId));
    }

    [Fact]
    public async Task The_lookup_is_ticked_to_begin_with()
    {
        // Most members are subscribed, and a looked-up sheet beats a typed one. The
        // note is what tells the rest what to do.
        var page = Page();
        await page.OnGetAsync(default);

        Assert.True(page.Input.UseHerald);
    }

    [Fact]
    public async Task Unticking_it_for_the_armory_records_what_was_typed()
    {
        // The whole point of the checkbox, through the page rather than the service:
        // a character the Armory will not show still gets recorded.
        var page = Page();
        page.Input = new IndexModel.InputModel
        {
            GamePresenceId = _armoryGameId,
            Name = "Lapsed",
            Class = "Frost Death Knight",
            Race = "Blood Elf",
            Level = 80,
            UseHerald = false,
        };

        var result = await page.OnPostAddAsync(default);

        // A redirect is the success path; a re-rendered page carries the error.
        Assert.IsType<RedirectToPageResult>(result);

        var stored = await Db.Characters
            .FirstAsync(c => c.GamePresenceId == _armoryGameId && c.Name == "Lapsed");

        Assert.Equal(CharacterSource.Manual, stored.Source);
        Assert.Equal("Frost Death Knight", stored.Class);
        Assert.Equal(80, stored.Level);

        // Race is on the form because a signature offers %Race% to every character,
        // and before this there was no way to fill it for a game with no herald.
        Assert.Equal("Blood Elf", stored.Race);
    }

    [Fact]
    public async Task Leaving_it_ticked_for_the_armory_asks_the_armory()
    {
        // No network here: the adapter is real, so it builds a URL and the fetcher
        // fails against a name with no realm. What is under test is that the page
        // took the lookup path at all.
        var page = Page();
        page.Input = new IndexModel.InputModel
        {
            GamePresenceId = _armoryGameId,
            Name = "Lapsed",
            UseHerald = true,
        };

        await page.OnPostAddAsync(default);

        Assert.NotNull(page.Error);
        Assert.Contains("realm", page.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(await Db.Characters.AnyAsync(c => c.GamePresenceId == _armoryGameId));
    }
}

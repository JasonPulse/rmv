using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;
using Rmv.Web.Tools.Spellcraft;

namespace Rmv.Web.Tests;

/// <summary>
/// The five template cap and the ownership scoping, against a real Postgres,
/// because half of what is being tested is a unique index and a check constraint.
///
/// Every call here goes straight to the store, which is exactly the shape of a
/// forged request: no page ran, no hidden field was read, no button was disabled.
/// If the cap only lived in the form these tests would sail past it.
/// </summary>
public class SpellcraftTemplateStoreTests : SpellcraftDatabaseTests
{
    private async Task<TemplateSaveOutcome> SaveAsync(string name, int? overwriteId = null) =>
        await Store.SaveAsync(Member.Id, name, Design("chest", "str-1"), overwriteId, default);

    private Task FillMineAsync() => FillToCapAsync(Member.Id);

    // --- saving --------------------------------------------------------------

    [Fact]
    public async Task Saving_records_the_design_under_the_members_own_id()
    {
        var outcome = await Store.SaveAsync(
            Member.Id, "  Resist chest  ", Design("chest", "str-1", "", "body-2"), null, default);

        Assert.True(outcome.Ok, outcome.Error);
        var saved = outcome.Template!;
        Assert.Equal("Resist chest", saved.Name);
        Assert.Equal(Member.Id, saved.MemberId);
        Assert.Equal(1, saved.Ordinal);

        Assert.True(SpellcraftDesign.TryDecode(saved.Design, out var back));
        Assert.Equal(["str-1", "", "body-2"], back.GemCodes);
    }

    [Fact]
    public async Task A_template_needs_a_name()
    {
        var blank = await SaveAsync("   ");
        Assert.False(blank.Ok);

        var overlong = await SaveAsync(new string('x', SpellcraftTemplate.MaxNameLength + 1));
        Assert.False(overlong.Ok);

        Assert.Equal(0, await CountAsync(Member.Id));
    }

    // --- the cap -------------------------------------------------------------

    [Fact]
    public async Task The_cap_holds_against_a_request_that_never_saw_the_form()
    {
        await FillMineAsync();

        // No page ran. This is the forged post: straight at the store, with no
        // overwrite chosen, by a member who is already full.
        var sixth = await Store.SaveAsync(Member.Id, "Sneaky sixth", Design(), null, default);

        Assert.False(sixth.Ok);
        Assert.True(sixth.NeedsOverwrite);
        Assert.Contains(SpellcraftTemplate.MaxPerMember.ToString(), sixth.Error!);
        Assert.Equal(SpellcraftTemplate.MaxPerMember, await CountAsync(Member.Id));
    }

    [Fact]
    public async Task Repeating_the_forged_save_never_gets_further()
    {
        await FillMineAsync();

        for (var i = 0; i < 4; i++)
        {
            Assert.False((await Store.SaveAsync(Member.Id, $"Try {i}", Design(), null, default)).Ok);
        }

        Assert.Equal(SpellcraftTemplate.MaxPerMember, await CountAsync(Member.Id));
    }

    [Fact]
    public async Task The_database_refuses_a_sixth_row_even_with_the_store_bypassed()
    {
        await FillMineAsync();

        // Behind the store entirely, which is as forged as it gets. The check
        // constraint on Ordinal is what stops this.
        Db.SpellcraftTemplates.Add(new SpellcraftTemplate
        {
            MemberId = Member.Id,
            Ordinal = SpellcraftTemplate.MaxPerMember + 1,
            Name = "Straight into the table",
            Design = Design().Encode(),
            SavedAt = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => Db.SaveChangesAsync());
        Db.ChangeTracker.Clear();

        Assert.Equal(SpellcraftTemplate.MaxPerMember, await CountAsync(Member.Id));
    }

    [Fact]
    public async Task Two_templates_cannot_share_an_ordinal()
    {
        var first = await SaveAsync("First");
        Assert.True(first.Ok, first.Error);

        Db.SpellcraftTemplates.Add(new SpellcraftTemplate
        {
            MemberId = Member.Id,
            Ordinal = first.Template!.Ordinal,
            Name = "Same slot",
            Design = Design().Encode(),
            SavedAt = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => Db.SaveChangesAsync());
        Db.ChangeTracker.Clear();
    }

    // --- overwriting ---------------------------------------------------------

    [Fact]
    public async Task At_the_cap_an_overwrite_replaces_rather_than_adds()
    {
        await FillMineAsync();

        var target = await Db.SpellcraftTemplates
            .AsNoTracking()
            .FirstAsync(t => t.MemberId == Member.Id && t.Ordinal == 3);

        var outcome = await Store.SaveAsync(
            Member.Id, "Replaced", Design("helm", "dex-2"), target.Id, default);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal(target.Id, outcome.Template!.Id);
        Assert.Equal(3, outcome.Template.Ordinal);
        Assert.Equal("Replaced", outcome.Template.Name);
        Assert.Equal(SpellcraftTemplate.MaxPerMember, await CountAsync(Member.Id));
    }

    [Fact]
    public async Task Deleting_one_frees_the_allowance_again()
    {
        await FillMineAsync();

        var third = await Db.SpellcraftTemplates
            .AsNoTracking()
            .FirstAsync(t => t.MemberId == Member.Id && t.Ordinal == 3);

        Assert.NotNull(await Store.DeleteAsync(Member.Id, third.Id, default));

        var replacement = await SaveAsync("Back to five");
        Assert.True(replacement.Ok, replacement.Error);
        // The freed ordinal is reused rather than the numbering marching upwards.
        Assert.Equal(3, replacement.Template!.Ordinal);
        Assert.Equal(SpellcraftTemplate.MaxPerMember, await CountAsync(Member.Id));
    }

    // --- ownership -----------------------------------------------------------

    [Fact]
    public async Task Overwriting_somebody_elses_template_is_refused()
    {
        var theirs = await Store.SaveAsync(Other.Id, "Not yours", Design(), null, default);
        Assert.True(theirs.Ok, theirs.Error);

        var attempt = await Store.SaveAsync(
            Member.Id, "Mine now", Design("helm"), theirs.Template!.Id, default);

        Assert.False(attempt.Ok);
        Assert.Contains("not one of yours", attempt.Error!);

        var untouched = await Db.SpellcraftTemplates.AsNoTracking()
            .FirstAsync(t => t.Id == theirs.Template.Id);
        Assert.Equal("Not yours", untouched.Name);
        Assert.Equal(Other.Id, untouched.MemberId);
    }

    [Fact]
    public async Task Deleting_somebody_elses_template_finds_nothing()
    {
        var theirs = await Store.SaveAsync(Other.Id, "Also not yours", Design(), null, default);
        Assert.True(theirs.Ok, theirs.Error);

        // The same answer as an id that does not exist, so this cannot be used to
        // discover what anyone else has saved.
        Assert.Null(await Store.DeleteAsync(Member.Id, theirs.Template!.Id, default));
        Assert.Null(await Store.DeleteAsync(Member.Id, 0, default));

        Assert.Equal(1, await CountAsync(Other.Id));
    }

    [Fact]
    public async Task Reading_one_by_id_is_scoped_to_the_owner()
    {
        var theirs = await Store.SaveAsync(Other.Id, "Private", Design(), null, default);
        Assert.True(theirs.Ok, theirs.Error);

        Assert.Null(await Store.FindAsync(Member.Id, theirs.Template!.Id, default));
        Assert.NotNull(await Store.FindAsync(Other.Id, theirs.Template.Id, default));
    }

    [Fact]
    public async Task The_list_shows_only_the_callers_own()
    {
        Assert.True((await Store.SaveAsync(Other.Id, "Theirs", Design(), null, default)).Ok);
        Assert.True((await SaveAsync("Mine")).Ok);

        var mine = await Store.ListAsync(Member.Id, default);

        Assert.Equal("Mine", Assert.Single(mine).Name);
    }

    [Fact]
    public async Task One_members_allowance_does_not_eat_into_anothers()
    {
        await FillMineAsync();

        var theirs = await Store.SaveAsync(Other.Id, "Room to spare", Design(), null, default);

        Assert.True(theirs.Ok, theirs.Error);
        Assert.Equal(1, theirs.Template!.Ordinal);
    }
}

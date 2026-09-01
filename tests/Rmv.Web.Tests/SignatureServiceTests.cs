using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Rmv.Web.Data;
using Rmv.Web.Signature;

namespace Rmv.Web.Tests;

/// <summary>
/// A member's signature, against a real Postgres.
///
/// The interesting property is not that it renders. It is when it does not: a
/// signature is embedded in forum posts and served to strangers, so the cost of
/// keeping it current has to land on a daily pass rather than on every view, and a
/// pass over a member who did nothing has to cost nothing.
/// </summary>
public class SignatureServiceTests : HeraldDatabaseTests
{
    private SignatureService _signatures = null!;
    private SignaturePresets _presets = null!;

    protected override void ConfigureHerald(FakeHeraldAdapter herald) =>
        herald.WithCharacter("Property");

    protected override Task SeedAsync()
    {
        var root = RepositoryRoot();

        _presets = new SignaturePresets(Path.Combine(root, "src", "Rmv.Web", "wwwroot"));
        _signatures = new SignatureService(
            Db,
            new SignatureRenderer(new SignatureFonts(
                Path.Combine(root, "src", "Rmv.Web", "Signature", "Fonts"))),
            _presets,
            NullLogger<SignatureService>.Instance);

        return Task.CompletedTask;
    }

    /// <summary>
    /// A design as one canonical string, for comparing two of them.
    ///
    /// Needed twice over. The column is jsonb, so Postgres normalises the document
    /// and what comes back is not the bytes that went in, which is fine and is worth
    /// having: the database itself then refuses a design that is not JSON. And
    /// SignatureDesign is a record whose Elements is a list, so record equality
    /// compares that member by reference rather than by contents.
    /// </summary>
    private static string Normalised(string json) =>
        SignatureService.Serialise(SignatureDesignReader.Read(json)!);


    /// <summary>One outlined line over a preset background, which is the shape both
    /// background tests want.</summary>
    private static SignatureDesign OverA(string presetKey) =>
        new(BackgroundKind.Preset, presetKey, "#000000",
        [
            new SignatureElement(10, 20, TextAlign.Left, SignatureFonts.DefaultKey, 18,
                "#ffffff", "#000000", null, "%User%"),
        ]);

    private async Task<Character> CharacterAsync(string name = "Property", int level = 50)
    {
        var c = new Character
        {
            MemberId = Member.Id,
            GamePresenceId = HeraldGameId,
            Name = name,
            Source = CharacterSource.Herald,
            Level = level,
            Class = "Skald",
            Realm = "Midgard",
            Score = 1_234_567,
            AddedAt = DateTimeOffset.UtcNow,
        };

        Db.Characters.Add(c);
        await Db.SaveChangesAsync();

        return c;
    }

    // --- creating one --------------------------------------------------------

    [Fact]
    public async Task A_member_gets_one_signature_with_the_default_design()
    {
        var character = await CharacterAsync();

        var signature = await _signatures.EnsureAsync(Member, default);

        Assert.Equal(Member.Id, signature.MemberId);
        Assert.Equal(Data.Signature.SlugLength, signature.Slug.Length);

        var design = SignatureDesignReader.Read(signature.Design);
        Assert.NotNull(design);
        Assert.NotEmpty(design.Elements);

        // Bound to the character they already had, so the default says something
        // true rather than drawing empty tokens.
        Assert.Equal(character.Id, design.Elements[0].CharacterId);
    }

    [Fact]
    public async Task Asking_twice_gets_the_same_signature_and_the_same_address()
    {
        var first = await _signatures.EnsureAsync(Member, default);
        var second = await _signatures.EnsureAsync(Member, default);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Slug, second.Slug);
        Assert.Equal(1, await Db.Signatures.CountAsync(s => s.MemberId == Member.Id));
    }

    [Fact]
    public async Task Two_members_get_different_addresses()
    {
        var other = await NewMemberAsync();

        var mine = await _signatures.EnsureAsync(Member, default);
        var theirs = await _signatures.EnsureAsync(other, default);

        Assert.NotEqual(mine.Slug, theirs.Slug);
    }

    [Fact]
    public void A_slug_says_nothing_about_the_account()
    {
        // A forum post carries this address for years, so it must not encode a member
        // id, and it must not be guessable by counting upwards.
        var slugs = Enumerable.Range(0, 200).Select(_ => SignatureService.NewSlug()).ToList();

        Assert.All(slugs, s => Assert.Equal(Data.Signature.SlugLength, s.Length));
        Assert.Equal(slugs.Count, slugs.Distinct().Count());

        // No vowels and no look-alikes, so one read aloud cannot become another.
        Assert.All(slugs, s => Assert.DoesNotMatch("[aeioulAEIOUL]", s));
    }

    // --- saving --------------------------------------------------------------

    [Fact]
    public async Task Saving_a_design_renders_it()
    {
        await CharacterAsync();
        var signature = await _signatures.EnsureAsync(Member, default);

        var outcome = await _signatures.SaveAsync(Member, signature.Design, default);

        Assert.True(outcome.Ok, outcome.Error);

        var image = await Db.SignatureImages.AsNoTracking()
            .FirstOrDefaultAsync(i => i.SignatureId == signature.Id);

        Assert.NotNull(image);
        Assert.NotEmpty(image.Bytes);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], image.Bytes[..4]);
        Assert.Equal(16, image.Version.Length);
        Assert.Equal(16, image.SourceVersion.Length);
    }

    [Fact]
    public async Task A_design_that_is_not_json_is_refused_without_touching_the_stored_one()
    {
        await CharacterAsync();
        var signature = await _signatures.EnsureAsync(Member, default);
        var before = Normalised(signature.Design);

        var outcome = await _signatures.SaveAsync(Member, "{not json", default);

        Assert.False(outcome.Ok);

        var after = Normalised((await Db.Signatures.AsNoTracking()
            .FirstAsync(s => s.Id == signature.Id)).Design);

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task The_column_itself_refuses_something_that_is_not_json()
    {
        // Belt and braces, and the reason the column is jsonb rather than text: a
        // design can only reach the database through SaveAsync today, and this is
        // what stops a future path from writing rubbish into it.
        var signature = await _signatures.EnsureAsync(Member, default);

        signature.Design = "{ not json at all";

        await Assert.ThrowsAsync<DbUpdateException>(() => Db.SaveChangesAsync());

        // The context is now holding a bad value, so drop it rather than leaving it
        // for the next test in this class.
        Db.Entry(signature).State = EntityState.Detached;
    }

    [Fact]
    public async Task A_design_larger_than_the_limit_is_refused()
    {
        await _signatures.EnsureAsync(Member, default);

        var outcome = await _signatures.SaveAsync(
            Member, new string('x', SignatureLimits.MaxDesignLength + 1), default);

        Assert.False(outcome.Ok);
        Assert.Contains("too large", outcome.Error);
    }

    [Fact]
    public async Task An_element_bound_to_somebody_elses_character_is_unbound_on_save()
    {
        // The check that matters: a design is JSON from a browser, so the character
        // id in it is a number somebody could change.
        var other = await NewMemberAsync();
        var theirs = new Character
        {
            MemberId = other.Id,
            GamePresenceId = HeraldGameId,
            Name = "Nottheirs",
            Source = CharacterSource.Manual,
            Level = 50,
            AddedAt = DateTimeOffset.UtcNow,
        };
        Db.Characters.Add(theirs);
        await Db.SaveChangesAsync();

        var design = new SignatureDesign(BackgroundKind.Colour, null, "#000000",
        [
            new SignatureElement(10, 10, TextAlign.Left, SignatureFonts.DefaultKey, 20,
                "#ffffff", null, theirs.Id, "%Name% level %Level%"),
        ]);

        var outcome = await _signatures.SaveAsync(
            Member, SignatureService.Serialise(design), default);

        Assert.True(outcome.Ok, outcome.Error);

        var stored = SignatureDesignReader.Read(outcome.Signature!.Design)!;
        Assert.Null(stored.Elements[0].CharacterId);
    }

    // --- when it re-renders, which is the point ------------------------------

    [Fact]
    public async Task A_pass_over_a_member_who_did_nothing_renders_nothing()
    {
        await CharacterAsync();
        var signature = await _signatures.EnsureAsync(Member, default);
        await _signatures.SaveAsync(Member, signature.Design, default);

        var before = await Db.SignatureImages.AsNoTracking()
            .FirstAsync(i => i.SignatureId == signature.Id);

        var changed = await _signatures.RefreshAsync(Member.Id, default);

        Assert.False(changed);

        var after = await Db.SignatureImages.AsNoTracking()
            .FirstAsync(i => i.SignatureId == signature.Id);

        // Nothing moved, so a browser holding the old ETag is still right.
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.RenderedAt, after.RenderedAt);
    }

    [Fact]
    public async Task A_character_levelling_redraws_the_signature()
    {
        var character = await CharacterAsync(level: 49);
        var signature = await _signatures.EnsureAsync(Member, default);
        await _signatures.SaveAsync(Member, signature.Design, default);

        var before = await Db.SignatureImages.AsNoTracking()
            .FirstAsync(i => i.SignatureId == signature.Id);

        character.Level = 50;
        await Db.SaveChangesAsync();

        Assert.True(await _signatures.RefreshAsync(Member.Id, default));

        var after = await Db.SignatureImages.AsNoTracking()
            .FirstAsync(i => i.SignatureId == signature.Id);

        Assert.NotEqual(before.Version, after.Version);
        Assert.NotEqual(before.SourceVersion, after.SourceVersion);
    }

    [Fact]
    public async Task Adding_a_character_changes_the_count_the_signature_draws()
    {
        // The cross-herald tokens are what makes this different from the old ones:
        // %AllChars% moves when a member does something, not when a herald does.
        await CharacterAsync();

        var design = new SignatureDesign(BackgroundKind.Colour, null, "#0a0c12",
        [
            new SignatureElement(10, 20, TextAlign.Left, SignatureFonts.DefaultKey, 18,
                "#ffffff", null, null, "%User% has played %AllChars% characters in %AllGames% games"),
        ]);

        var saved = await _signatures.SaveAsync(Member, SignatureService.Serialise(design), default);
        Assert.True(saved.Ok, saved.Error);

        var before = await Db.SignatureImages.AsNoTracking()
            .FirstAsync(i => i.SignatureId == saved.Signature!.Id);

        // A second character, on a second game.
        Db.Characters.Add(new Character
        {
            MemberId = Member.Id,
            GamePresenceId = NoHeraldGameId,
            Name = "Second",
            Source = CharacterSource.Manual,
            Level = 20,
            AddedAt = DateTimeOffset.UtcNow,
        });
        await Db.SaveChangesAsync();

        Assert.True(await _signatures.RefreshAsync(Member.Id, default));

        var after = await Db.SignatureImages.AsNoTracking()
            .FirstAsync(i => i.SignatureId == saved.Signature!.Id);

        Assert.NotEqual(before.Version, after.Version);
    }

    [Fact]
    public async Task A_change_that_does_not_show_moves_no_bytes()
    {
        // A kill count nobody put in a template changes the character and not the
        // picture. The source digest is over what the elements resolve to, so this
        // costs one query and no render.
        var character = await CharacterAsync();

        var design = new SignatureDesign(BackgroundKind.Colour, null, "#0a0c12",
        [
            new SignatureElement(10, 20, TextAlign.Left, SignatureFonts.DefaultKey, 18,
                "#ffffff", null, character.Id, "%Name% the %Class%"),
        ]);

        var saved = await _signatures.SaveAsync(Member, SignatureService.Serialise(design), default);
        var before = await Db.SignatureImages.AsNoTracking()
            .FirstAsync(i => i.SignatureId == saved.Signature!.Id);

        character.Kills = 99_999;
        await Db.SaveChangesAsync();

        await _signatures.RefreshAsync(Member.Id, default);

        var after = await Db.SignatureImages.AsNoTracking()
            .FirstAsync(i => i.SignatureId == saved.Signature!.Id);

        Assert.Equal(before.Version, after.Version);
    }

    [Fact]
    public async Task A_member_with_no_signature_is_not_an_error_to_refresh()
    {
        Assert.False(await _signatures.RefreshAsync(Member.Id, default));
    }

    // --- backgrounds ---------------------------------------------------------

    [Fact]
    public async Task A_preset_background_is_drawn()
    {
        await CharacterAsync();

        var preset = _presets.All.First();

        var design = OverA(preset.Key);

        var saved = await _signatures.SaveAsync(Member, SignatureService.Serialise(design), default);
        Assert.True(saved.Ok, saved.Error);

        var flat = new SignatureDesign(BackgroundKind.Colour, null, "#000000", design.Elements);
        var withoutBackground = await _signatures.SaveAsync(
            await NewMemberAsync(), SignatureService.Serialise(flat), default);

        var withPreset = await Db.SignatureImages.AsNoTracking()
            .FirstAsync(i => i.SignatureId == saved.Signature!.Id);
        var without = await Db.SignatureImages.AsNoTracking()
            .FirstAsync(i => i.SignatureId == withoutBackground.Signature!.Id);

        Assert.NotEqual(without.Version, withPreset.Version);
    }

    [Fact]
    public async Task A_preset_that_does_not_exist_becomes_no_background()
    {
        var design = new SignatureDesign(BackgroundKind.Preset, "../../../etc/passwd", "#000000",
        [
            new SignatureElement(10, 20, TextAlign.Left, SignatureFonts.DefaultKey, 18,
                "#ffffff", null, null, "%User%"),
        ]);

        var saved = await _signatures.SaveAsync(Member, SignatureService.Serialise(design), default);

        Assert.True(saved.Ok, saved.Error);

        var stored = SignatureDesignReader.Read(saved.Signature!.Design)!;
        Assert.Equal(BackgroundKind.Colour, stored.Background);
        Assert.Null(stored.BackgroundKey);
    }

    [Fact]
    public async Task Every_shipped_preset_reads_and_renders()
    {
        // Twenty-two files copied out of a 2014 backup, two of them jpg. A preset
        // that will not decode is a background that silently does not appear.
        await CharacterAsync();

        Assert.NotEmpty(_presets.All);

        foreach (var preset in _presets.All)
        {
            var bytes = _presets.Read(preset.Key);

            Assert.NotNull(bytes);
            Assert.NotEmpty(bytes);

            var design = OverA(preset.Key);

            var saved = await _signatures.SaveAsync(
                Member, SignatureService.Serialise(design), default);

            Assert.True(saved.Ok, $"{preset.Key}: {saved.Error}");
        }
    }
}

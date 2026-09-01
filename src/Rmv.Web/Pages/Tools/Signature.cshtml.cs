using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;
using Rmv.Web.Signature;

namespace Rmv.Web.Pages.Tools;

/// <summary>
/// The signature editor.
///
/// Approved members only, which is the part the old one had no notion of: it kept a
/// design per browser cookie, so a design belonged to whoever guessed its number and
/// a member who cleared their cookies lost it.
///
/// The image itself is public and served by SignatureEndpoint, because a forum sends
/// no cookies. This page only edits.
/// </summary>
[Authorize(Policy = MemberPolicy.Approved)]
public class SignatureModel(
    RmvDbContext db,
    CurrentMember me,
    SignatureService signatures,
    SignaturePresets presets,
    SignatureFonts fonts,
    Rmv.Web.Herald.HeraldStatTokens heraldStats) : PageModel
{
    /// <summary>The design, as JSON. Posted back as JSON, clamped on the way in.</summary>
    [BindProperty]
    public string? Design { get; set; }

    public Data.Signature? Signature { get; private set; }

    /// <summary>Where a forum points. The whole reason the page exists.</summary>
    public string? Address { get; private set; }

    public string? Embed { get; private set; }

    /// <summary>What the last render came out as, so the page can show it.</summary>
    public string? ImagePath { get; private set; }

    public DateTimeOffset? RenderedAt { get; private set; }

    public IReadOnlyList<Character> Characters { get; private set; } = [];

    public IReadOnlyList<SignaturePreset> Presets => presets.All;

    public IReadOnlyList<SignatureBackground> Uploads { get; private set; } = [];

    public IReadOnlyList<SignatureToken> Tokens => SignatureTokens.All;

    /// <summary>
    /// What each herald publishes on top of the character sheet, grouped so a member
    /// can see that relics are a DAoC thing and master level is not.
    ///
    /// Only the heralds they have a character on. Offering all four meant offering
    /// %ItemLevel% to somebody with no WoW character, where it would draw nothing on
    /// any line they put it on.
    /// </summary>
    public IReadOnlyList<Rmv.Web.Herald.HeraldStatTokens.Group> HeraldTokens { get; private set; } = [];

    public IReadOnlyCollection<string> Fonts => fonts.Keys;

    /// <summary>
    /// The characters an element can be bound to, for the editor's dropdown.
    ///
    /// Two of his characters are both called Milliennial on different games, so the
    /// label has to carry the game or the list is unusable.
    /// </summary>
    public string CharactersJson => System.Text.Json.JsonSerializer.Serialize(
        Characters.Select(c => new
        {
            id = c.Id,
            label = c.Game is null ? c.Name : $"{c.Name} ({c.Game.Game})",
        }));

    public string FontsJson => System.Text.Json.JsonSerializer.Serialize(Fonts);

    /// <summary>
    /// What each line will actually say, so the canvas shows real text rather than
    /// the tokens somebody typed.
    ///
    /// This matters for placement and it is the whole reason it exists: %Name%%SP%
    /// is ten characters and "Milliennial - " is fourteen, so a line positioned
    /// against the tokens lands somewhere else once it is drawn. Resolved here rather
    /// than in the browser, by the same code the renderer uses, so the preview cannot
    /// disagree with the picture.
    /// </summary>
    public string PreviewJson { get; private set; } = "[]";

    public int Width => SignatureLimits.Width;

    public int Height => SignatureLimits.Height;

    public int MaxElements => SignatureLimits.MaxElements;

    public int MaxTemplate => SignatureLimits.MaxTemplate;

    public int MaxBackgrounds => SignatureLimits.MaxBackgrounds;

    public string? Notice { get; private set; }

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await me.GetAsync(User, ct) is not { } member)
        {
            return Forbid();
        }

        await LoadAsync(member, ct);

        Notice = this.Flash("saved") is not null ? "Saved. Your signature is updated everywhere it is embedded."
            : this.Flash("uploaded") is not null ? "Background added."
            : this.Flash("removed") is not null ? "Background removed."
            : this.Flash("reset") is not null ? "Back to the default design." : null;

        Error = this.Flash("error");

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        if (await me.GetAsync(User, ct) is not { } member)
        {
            return Forbid();
        }

        var outcome = await signatures.SaveAsync(member, Design, ct);

        if (!outcome.Ok)
        {
            Error = outcome.Error;
            await LoadAsync(member, ct);
            return Page();
        }

        return RedirectToPage(new { saved = true });
    }

    /// <summary>
    /// Back to the design a new member gets.
    ///
    /// Worth having because the editor is the kind of thing somebody drags into a
    /// mess, and the alternative is asking an admin to fix a row.
    /// </summary>
    public async Task<IActionResult> OnPostResetAsync(CancellationToken ct)
    {
        if (await me.GetAsync(User, ct) is not { } member)
        {
            return Forbid();
        }

        await signatures.SaveAsync(
            member,
            SignatureService.Serialise(await signatures.DefaultDesignAsync(member, ct)),
            ct);

        return RedirectToPage(new { reset = true });
    }

    /// <summary>
    /// Removes one of the member's own backgrounds.
    ///
    /// Scoped to them in the query, so an id from somewhere else is not found rather
    /// than found and then refused.
    /// </summary>
    public async Task<IActionResult> OnPostRemoveBackgroundAsync(int id, CancellationToken ct)
    {
        if (await me.GetAsync(User, ct) is not { } member)
        {
            return Forbid();
        }

        var background = await db.SignatureBackgrounds
            .FirstOrDefaultAsync(b => b.Id == id && b.MemberId == member.Id, ct);

        if (background is not null)
        {
            db.SignatureBackgrounds.Remove(background);
            await db.SaveChangesAsync(ct);

            // A design pointing at what just went away would draw the flat colour
            // anyway, but re-rendering now means the picture matches the page.
            await signatures.RefreshAsync(member.Id, ct);
        }

        return RedirectToPage(new { removed = true });
    }

    /// <summary>
    /// The resolved text for a design the editor is still editing.
    ///
    /// Called as somebody types, debounced, and answers with one string per line.
    /// The same SignatureTokens the renderer uses, against their real characters, so
    /// what the canvas shows is what the PNG will say.
    /// </summary>
    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken ct)
    {
        if (await me.GetAsync(User, ct) is not { } member)
        {
            return Forbid();
        }

        if (Design is null || Design.Length > SignatureLimits.MaxDesignLength)
        {
            return BadRequest();
        }

        if (SignatureDesignReader.Read(Design) is not { } design)
        {
            return BadRequest();
        }

        var roster = await RosterAsync(member, ct);

        return new JsonResult(design.Elements
            .Take(SignatureLimits.MaxElements)
            .Select(e => SignatureTokens.Resolve(
                e.Template, SignatureData.Subject(member, roster, e.CharacterId)))
            .ToList());
    }

    /// <summary>
    /// One of the member's own backgrounds, for the editor's canvas.
    ///
    /// A handler rather than a public endpoint: this is somebody's own picture and
    /// the page's policy is what protects it. Scoped to them in the query, so an id
    /// from elsewhere is not found rather than found and refused.
    /// </summary>
    public async Task<IActionResult> OnGetBackgroundAsync(int id, CancellationToken ct)
    {
        if (await me.GetAsync(User, ct) is not { } member)
        {
            return Forbid();
        }

        if (await signatures.BackgroundAsync(member.Id, id, ct) is not { } background)
        {
            return NotFound();
        }

        // Private: this is not on the roster and not for an edge cache.
        Response.Headers.CacheControl = "private, max-age=300";

        return File(background.Bytes, background.ContentType);
    }

    /// <summary>
    /// The member's characters, ordered for the editor's dropdown, and the same list
    /// the preview resolves against.
    /// </summary>
    private Task<List<Character>> RosterAsync(Member member, CancellationToken ct) =>
        db.Characters
            .Include(c => c.Game)
            .Where(c => c.MemberId == member.Id)
            .OrderBy(c => c.Game!.Game)
            .ThenBy(c => c.Name)
            .AsNoTracking()
            .ToListAsync(ct);

    private async Task LoadAsync(Member member, CancellationToken ct)
    {
        Signature = await signatures.EnsureAsync(member, ct);

        Design ??= Signature.Design;

        Address = $"{Request.Scheme}://{Request.Host}{SignatureEndpoint.PathFor(Signature.Slug)}";
        Embed = $"[img]{Address}[/img]";
        ImagePath = SignatureEndpoint.PathFor(Signature.Slug);
        RenderedAt = Signature.Image?.RenderedAt;

        Characters = await RosterAsync(member, ct);

        HeraldTokens = heraldStats.For(Characters.Select(c => c.Game?.HeraldAdapterKey));

        // The first preview, so the canvas never shows raw tokens even for a moment.
        if (SignatureDesignReader.Read(Design) is { } design)
        {
            PreviewJson = System.Text.Json.JsonSerializer.Serialize(design.Elements
                .Take(SignatureLimits.MaxElements)
                .Select(e => SignatureTokens.Resolve(
                    e.Template, SignatureData.Subject(member, Characters, e.CharacterId))));
        }

        Uploads = await db.SignatureBackgrounds
            .Where(b => b.MemberId == member.Id)
            .OrderBy(b => b.UploadedAt)
            .AsNoTracking()
            .ToListAsync(ct);
    }
}

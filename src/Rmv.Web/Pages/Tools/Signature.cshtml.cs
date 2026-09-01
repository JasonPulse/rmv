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
    SignatureFonts fonts) : PageModel
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

    private async Task LoadAsync(Member member, CancellationToken ct)
    {
        Signature = await signatures.EnsureAsync(member, ct);

        Design ??= Signature.Design;

        Address = $"{Request.Scheme}://{Request.Host}{SignatureEndpoint.PathFor(Signature.Slug)}";
        Embed = $"[img]{Address}[/img]";
        ImagePath = SignatureEndpoint.PathFor(Signature.Slug);
        RenderedAt = Signature.Image?.RenderedAt;

        Characters = await db.Characters
            .Include(c => c.Game)
            .Where(c => c.MemberId == member.Id)
            .OrderBy(c => c.Game!.Game)
            .ThenBy(c => c.Name)
            .AsNoTracking()
            .ToListAsync(ct);

        Uploads = await db.SignatureBackgrounds
            .Where(b => b.MemberId == member.Id)
            .OrderBy(b => b.UploadedAt)
            .AsNoTracking()
            .ToListAsync(ct);
    }
}

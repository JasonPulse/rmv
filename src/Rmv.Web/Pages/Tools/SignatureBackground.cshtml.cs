using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Rmv.Web.Data;
using Rmv.Web.Signature;
using Rmv.Web.Tools;

namespace Rmv.Web.Pages.Tools;

/// <summary>
/// Uploading a signature background.
///
/// Its own page for one reason: Razor Pages ignores [EnableRateLimiting] on a
/// handler method, and the compiler now refuses to let one be written. Putting the
/// attribute on the editor's class would throttle looking at the editor, which is
/// the part that should be free. So the form on that page posts here, and this page
/// has no UI of its own; a GET goes back to the editor.
///
/// Same shape as Gallery/Add, for the same reason and with the same limits: this is
/// one of two places on the site where a member can make the server do work
/// proportional to what they send.
/// </summary>
[Authorize(Policy = MemberPolicy.Approved)]
[EnableRateLimiting(RateLimitPolicies.Upload)]
public class SignatureBackgroundModel(
    CurrentMember me, SignatureService signatures) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Tools/Signature");

    public async Task<IActionResult> OnPostAsync(IFormFile? file, CancellationToken ct)
    {
        if (await me.GetAsync(User, ct) is not { } member)
        {
            return Forbid();
        }

        if (file is not { Length: > 0 })
        {
            return RedirectToPage("/Tools/Signature", new { error = "Pick a picture to upload." });
        }

        await using var stream = file.OpenReadStream();

        // Nothing about the file but its bytes is passed on: not its name for
        // anything that matters, not its extension, not the type it claims.
        var outcome = await signatures.AddBackgroundAsync(
            member, stream, file.Length, file.FileName, ct);

        return RedirectToPage("/Tools/Signature", outcome.Ok
            ? new { uploaded = true }
            : new { error = outcome.Error });
    }
}

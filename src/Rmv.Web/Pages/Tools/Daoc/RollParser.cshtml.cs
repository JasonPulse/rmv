using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Rmv.Web.Tools;

namespace Rmv.Web.Pages.Tools.Daoc;

/// <summary>
/// Accepts a chat log and reports /random results, highest first.
///
/// The uploaded file is never written to disk and never leaves this request. It
/// is read straight from the request stream, parsed line by line, and only
/// values matching the parser's pattern survive into the model. Nothing about
/// the file other than its length is trusted: not its name, not its extension,
/// not its declared content type.
/// </summary>
[EnableRateLimiting(RateLimitPolicies.Upload)]
[RequestSizeLimit(MaxRequestBytes)]
public class RollParserModel : PageModel
{
    /// <summary>A 2MB log is roughly 25k lines, far more than any raid produces.</summary>
    public const int MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>The file cap plus room for multipart framing.</summary>
    private const int MaxRequestBytes = MaxFileBytes + 64 * 1024;

    /// <summary>Pasting is bounded well below the file cap; it is for a handful of lines.</summary>
    public const int MaxPasteChars = 256 * 1024;

    [BindProperty]
    public IFormFile? LogFile { get; set; }

    [BindProperty]
    public string? Pasted { get; set; }

    public RollReport? Report { get; private set; }

    public string? Error { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var hasFile = LogFile is { Length: > 0 };
        var hasPaste = !string.IsNullOrWhiteSpace(Pasted);

        if (hasFile && hasPaste)
        {
            Error = "Pick one: upload a file or paste text, not both.";
            return Page();
        }

        if (hasFile)
        {
            if (LogFile!.Length > MaxFileBytes)
            {
                Error = $"That file is {LogFile.Length / 1024 / 1024}MB. The limit is {MaxFileBytes / 1024 / 1024}MB.";
                return Page();
            }

            // Straight from the request stream. Nothing touches disk.
            await using var stream = LogFile.OpenReadStream();
            Report = RollParser.Parse(stream);
        }
        else if (hasPaste)
        {
            if (Pasted!.Length > MaxPasteChars)
            {
                Error = "That is too much text to paste. Upload it as a file instead.";
                return Page();
            }

            Report = RollParser.Parse(new StringReader(Pasted));
        }
        else
        {
            Error = "Choose a log file, or paste some lines from one.";
            return Page();
        }

        if (Report.RollsFound == 0)
        {
            Error = "No rolls found. Chat logging has to be on, and the log needs "
                  + "lines like \"Playername picks a random number between 1 and 100: 87\".";
        }

        return Page();
    }
}

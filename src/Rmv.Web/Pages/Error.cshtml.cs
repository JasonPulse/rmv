using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Rmv.Web.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
    public string? RequestId { get; private set; }

    /// <summary>
    /// Set by UseStatusCodePagesWithReExecute for 404s and the like. Null when
    /// the page is reached through the exception handler.
    /// </summary>
    [FromQuery(Name = "code")]
    public int? Code { get; set; }

    public string Heading => Code switch
    {
        404 => "No such page",
        403 => "Barred",
        >= 500 => "Something broke",
        not null => $"Error {Code}",
        _ => "Something broke",
    };

    public string Detail => Code switch
    {
        404 => "That path does not exist. It may never have.",
        403 => "You are signed in but not permitted here.",
        _ => "The page could not be built. If it keeps happening, tell Jason.",
    };

    public void OnGet() => RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
}

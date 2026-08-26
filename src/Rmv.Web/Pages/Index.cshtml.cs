using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Rmv.Web.Pages;

/// <summary>
/// Deliberately has no dependencies. The home page is the one every visitor
/// sees, so it does not read the database and cannot fail because of it.
/// </summary>
public class IndexModel : PageModel
{
}

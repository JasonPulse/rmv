using Microsoft.AspNetCore.Mvc.RazorPages;
using Rmv.Web.Content;

namespace Rmv.Web.Pages.News;

/// <summary>
/// The news listing. Public, and reads the filesystem rather than the database, so
/// it renders whether or not Postgres is up.
/// </summary>
public class IndexModel(NewsLibrary news) : PageModel
{
    public IReadOnlyList<NewsPost> Posts { get; private set; } = [];

    public void OnGet() => Posts = news.All();
}

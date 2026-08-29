using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Rmv.Web.Content;

namespace Rmv.Web.Pages.News;

public class PostModel(NewsLibrary news) : PageModel
{
    public NewsPost? Post { get; private set; }

    public IActionResult OnGet(string slug)
    {
        // Looked up in the index rather than by building a path, so a slug cannot
        // reach the filesystem. See NewsLibrary.Find.
        Post = news.Find(slug);

        return Post is null ? NotFound() : Page();
    }
}

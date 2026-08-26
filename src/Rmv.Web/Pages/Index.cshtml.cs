using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Rmv.Web.Data;

namespace Rmv.Web.Pages;

public class IndexModel(IDeploymentStore store) : PageModel
{
    public StatusView Status { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct)
        => Status = await store.ReadAsync(ct);

    /// <summary>
    /// htmx target. Returns just the status panel fragment so the page can
    /// refresh it without a reload. Reached as GET /?handler=Status.
    /// </summary>
    public async Task<IActionResult> OnGetStatusAsync(CancellationToken ct)
        => Partial("_StatusPanel", await store.ReadAsync(ct));
}

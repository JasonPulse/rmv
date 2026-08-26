using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Rmv.Web.Data;

namespace Rmv.Web.Pages;

/// <summary>
/// Operator diagnostics: build, host, boot count, database state. Not for
/// visitors, so it is authorized in Production by a convention in Program.cs and
/// left open in Development.
/// </summary>
public class StatusModel(IDeploymentStore store) : PageModel
{
    public StatusView Status { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct)
        => Status = await store.ReadAsync(ct);

    /// <summary>htmx target. GET /status?handler=Panel returns just the panel.</summary>
    public async Task<IActionResult> OnGetPanelAsync(CancellationToken ct)
        => Partial("_StatusPanel", await store.ReadAsync(ct));
}

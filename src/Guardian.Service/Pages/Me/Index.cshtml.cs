using Guardian.Service.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Guardian.Service.Pages.Me;

public class IndexModel : PageModel
{
    private readonly Queries _q;
    public IndexModel(Queries q) => _q = q;
    public TodayView View { get; private set; } = null!;
    public void OnGet() => View = _q.Today();
    public IActionResult OnGetPanel() => Partial("_TodayPanel", _q.Today());
}

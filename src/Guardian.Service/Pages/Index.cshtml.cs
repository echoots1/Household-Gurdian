using Guardian.Service.Infrastructure;
using Guardian.Service.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Guardian.Service.Pages;

public class IndexModel : PageModel
{
    private readonly Queries _q; private readonly GuardianState _state;
    public IndexModel(Queries q, GuardianState state) { _q = q; _state = state; }
    public TodayView View { get; private set; } = null!;
    public string Version => _state.InstalledVersion;
    public DateTimeOffset StartedAt => _state.StartedAt;

    public void OnGet(string? date) => View = _q.Today(date is null ? null : DateOnly.Parse(date));
    public IActionResult OnGetPanel(string? date) => Partial("_TodayPanel", _q.Today(date is null ? null : DateOnly.Parse(date)));
}

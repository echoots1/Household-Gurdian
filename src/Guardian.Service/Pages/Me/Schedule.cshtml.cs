using Guardian.Service.Web;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Guardian.Service.Pages.Me;

public class ScheduleModel : PageModel
{
    private readonly Queries _q;
    public ScheduleModel(Queries q) => _q = q;
    public ScheduleView View { get; private set; } = null!;
    public void OnGet() => View = _q.Schedule();
}

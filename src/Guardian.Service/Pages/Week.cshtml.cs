using Guardian.Service.Web;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Guardian.Service.Pages;

public class WeekModel : PageModel
{
    private readonly Queries _q;
    public WeekModel(Queries q) => _q = q;
    public WeekView View { get; private set; } = null!;
    public void OnGet(string? start) => View = _q.Week(start is null ? null : DateOnly.Parse(start));
}

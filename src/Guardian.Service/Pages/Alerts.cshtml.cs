using Guardian.Contracts;
using Guardian.Core.Storage;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Guardian.Service.Pages;

public class AlertsModel : PageModel
{
    private readonly AlertRepo _alerts;
    public AlertsModel(AlertRepo alerts) => _alerts = alerts;
    public bool All { get; private set; }
    public IReadOnlyList<Alert> Rows { get; private set; } = Array.Empty<Alert>();
    public void OnGet(int? all) { All = all == 1; Rows = _alerts.List(!All, 300); }

    public static string Icon(string type) => type switch { AlertType.ContentFlag => "⚠", AlertType.TimeRequest => "🙋", AlertType.TrayRelaunch => "🔁", AlertType.ClockJump => "🕰", AlertType.BackupFailed => "💾", _ => "•" };
    public static string Describe(Alert a) => a.Type switch
    {
        AlertType.ContentFlag => $"Content flag: {a.Str("domain")} is on the {a.Str("category")} list" + (a.Str("title") is { Length: > 0 } t ? $" — \"{t}\"" : ""),
        AlertType.TimeRequest => "Request for more time" + (a.Str("reason") is { Length: > 0 } r ? $": \"{r}\"" : " (no reason given)"),
        AlertType.TrayRelaunch => "The tray was not running and was relaunched (not treated as an offense)",
        AlertType.ClockJump => "The system clock moved backwards; the day boundary was kept",
        AlertType.BackupFailed => $"Backup to {a.Str("path")} failed: {a.Str("error")}",
        AlertType.ExtensionMissing => "Browser extension not reporting; address-bar fallback in use",
        _ => a.Type,
    };
}

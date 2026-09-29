using Guardian.Contracts;
using Guardian.Core.Auth;
using Guardian.Core.Storage;
using Guardian.Service.Infrastructure;
using Guardian.Service.Win32;
using Guardian.Service.Workers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Guardian.Service.Pages;

/// <summary>The first-run wizard. Loopback only; only until setup is completed. Re-run by deleting the setup_completed setting (the installer does that on "change monitored account").</summary>
public class SetupModel : PageModel
{
    private readonly SettingsRepo _settings; private readonly ISessionControl _session; private readonly GuardianState _state; private readonly SchedulerWorker _sched;
    public SetupModel(SettingsRepo settings, ISessionControl session, GuardianState state, SchedulerWorker sched) { _settings = settings; _session = session; _state = state; _sched = sched; }

    public bool Done => _settings.SetupCompleted;
    public string? Error { get; private set; }
    public IReadOnlyList<(string name, bool isAdmin)> Users { get; private set; } = Array.Empty<(string, bool)>();
    public string MonitoredUser => _settings.MonitoredUser;
    public string Host => System.Net.Dns.GetHostName();
    public IEnumerable<string> LanAddresses => Certificates.LanAddresses().Select(a => a.ToString());
    public string Fingerprint => _state.CertFingerprint;

    public void OnGet() => Users = _session.LocalUsers();

    public IActionResult OnPost(string? user, string? userOther, string password, string password2, string? smtpHost, int? smtpPort, string? smtpUser, string? smtpPassword,
        string? smtpFrom, string? smtpTo, string? backupPath, string? backupUser, string? backupPassword)
    {
        if (Done) return RedirectToPage();
        Users = _session.LocalUsers();
        var account = string.IsNullOrWhiteSpace(userOther) ? user : userOther.Trim();
        if (string.IsNullOrWhiteSpace(account)) { Error = "Pick the account to monitor."; return Page(); }
        if (password != password2) { Error = "The passwords do not match."; return Page(); }
        if (PasswordHasher.Validate(password) is { } err) { Error = err; return Page(); }

        _settings.Set(SettingKeys.MonitoredUser, account);
        _settings.Set(SettingKeys.MonitoredUserIsAdmin, _session.IsAdministrator(account) ? "1" : "0");
        _settings.Set(SettingKeys.ParentPasswordHash, PasswordHasher.Hash(password));
        _settings.Set(SettingKeys.SmtpHost, Blank(smtpHost)); _settings.Set(SettingKeys.SmtpPort, smtpPort?.ToString()); _settings.Set(SettingKeys.SmtpUser, Blank(smtpUser));
        if (!string.IsNullOrEmpty(smtpPassword)) _settings.Set(SettingKeys.SmtpPassword, Secrets.Protect(smtpPassword));
        _settings.Set(SettingKeys.SmtpFrom, Blank(smtpFrom)); _settings.Set(SettingKeys.SmtpTo, Blank(smtpTo));
        _settings.Set(SettingKeys.BackupPath, Blank(backupPath)); _settings.Set(SettingKeys.BackupUser, Blank(backupUser));
        if (!string.IsNullOrEmpty(backupPassword)) _settings.Set(SettingKeys.BackupPassword, Secrets.Protect(backupPassword));
        _settings.Set(SettingKeys.SetupCompleted, "1");
        _sched.RunOnce();
        return RedirectToPage();
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

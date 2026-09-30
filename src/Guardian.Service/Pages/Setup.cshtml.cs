using Guardian.Contracts;
using Guardian.Core.Auth;
using Guardian.Core.Storage;
using Guardian.Service.Infrastructure;
using Guardian.Service.Web;
using Guardian.Service.Win32;
using Guardian.Service.Workers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Guardian.Service.Pages;

/// <summary>The first-run wizard. Loopback only; only until setup is completed. Re-run by deleting the setup_completed setting (the installer does that on "change monitored account").</summary>
[IgnoreAntiforgeryToken] // this page uses SetupNonces (a server-side synchronizer token) instead of the cookie-based antiforgery token
public class SetupModel : PageModel
{
    private readonly SettingsRepo _settings; private readonly ISessionControl _session; private readonly GuardianState _state; private readonly SchedulerWorker _sched; private readonly ILogger<SetupModel> _log; private readonly SetupNonces _nonces;
    public SetupModel(SettingsRepo settings, ISessionControl session, GuardianState state, SchedulerWorker sched, ILogger<SetupModel> log, SetupNonces nonces) { _settings = settings; _session = session; _state = state; _sched = sched; _log = log; _nonces = nonces; }
    public string Nonce { get; private set; } = "";

    public bool Done => _settings.SetupCompleted;
    public string? Error { get; private set; }
    public IReadOnlyList<(string name, bool isAdmin)> Users { get; private set; } = Array.Empty<(string, bool)>();
    public string MonitoredUser => _settings.MonitoredUser;
    public string Host => System.Net.Dns.GetHostName();
    public IEnumerable<string> LanAddresses => Certificates.LanAddresses().Select(a => a.ToString());
    public string Fingerprint => _state.CertFingerprint;

    public void OnGet() { Users = _session.LocalUsers(); Nonce = _nonces.Issue(); }

    public IActionResult OnPost(string? setupNonce, string? user, string? userOther, string? password, string? password2, string? smtpHost, int? smtpPort, string? smtpUser, string? smtpPassword,
        string? smtpFrom, string? smtpTo, string? backupPath, string? backupUser, string? backupPassword)
    {
        if (Done) return RedirectToPage();
        Users = _session.LocalUsers();
        if (!_nonces.Consume(setupNonce))
        {
            _log.LogWarning("Setup form rejected: unknown or already-used setup token (service restarted, or the page was opened before this install)");
            Error = "This page had expired (the service may have restarted). Please fill it in once more.";
            Nonce = _nonces.Issue();
            return Page();
        }
        password ??= ""; password2 ??= "";
        var account = string.IsNullOrWhiteSpace(userOther) ? user : userOther.Trim();
        if (string.IsNullOrWhiteSpace(account)) { Error = "Pick the account to monitor."; Nonce = _nonces.Issue(); return Page(); }
        if (password != password2) { Error = "The passwords do not match."; Nonce = _nonces.Issue(); return Page(); }
        if (PasswordHasher.Validate(password) is { } err) { Error = err; Nonce = _nonces.Issue(); return Page(); }

        _settings.Set(SettingKeys.MonitoredUser, account);
        _settings.Set(SettingKeys.MonitoredUserIsAdmin, _session.IsAdministrator(account) ? "1" : "0");
        _settings.Set(SettingKeys.ParentPasswordHash, PasswordHasher.Hash(password));
        _settings.Set(SettingKeys.SmtpHost, Blank(smtpHost)); _settings.Set(SettingKeys.SmtpPort, smtpPort?.ToString()); _settings.Set(SettingKeys.SmtpUser, Blank(smtpUser));
        if (!string.IsNullOrEmpty(smtpPassword)) _settings.Set(SettingKeys.SmtpPassword, Secrets.Protect(smtpPassword));
        _settings.Set(SettingKeys.SmtpFrom, Blank(smtpFrom)); _settings.Set(SettingKeys.SmtpTo, Blank(smtpTo));
        _settings.Set(SettingKeys.BackupPath, Blank(backupPath)); _settings.Set(SettingKeys.BackupUser, Blank(backupUser));
        if (!string.IsNullOrEmpty(backupPassword)) _settings.Set(SettingKeys.BackupPassword, Secrets.Protect(backupPassword));
        _settings.Set(SettingKeys.SetupCompleted, "1");
        _log.LogInformation("Setup completed: monitoring account '{Account}' (admin={Admin}); stored value reads back as '{Stored}'", account, _settings.Get(SettingKeys.MonitoredUserIsAdmin), _settings.MonitoredUser);
        _sched.RunOnce();
        return RedirectToPage();
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

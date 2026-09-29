using Guardian.Contracts;
using Guardian.Core.Storage;
using Guardian.Core.Time;
using Guardian.Service.Infrastructure;
using Guardian.Service.Web;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Guardian.Service.Pages;

public class SettingsModel : PageModel
{
    private readonly SettingsRepo _settings; private readonly RuleRepo _rules; private readonly ListRepo _lists; private readonly GuardianState _state; private readonly Paths _paths;
    public SettingsModel(SettingsRepo settings, RuleRepo rules, ListRepo lists, GuardianState state, Paths paths) { _settings = settings; _rules = rules; _lists = lists; _state = state; _paths = paths; }

    public Dictionary<string, string?> Values { get; private set; } = new();
    public IReadOnlyList<Rule> Rules { get; private set; } = Array.Empty<Rule>();
    public Dictionary<string, int> ListCounts { get; private set; } = new();
    public IReadOnlyList<string> Custom { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<(string domain, string mode)> Overrides { get; private set; } = Array.Empty<(string, string)>();
    public DateTimeOffset? NoticeAcceptedAt => _settings.NoticeAcceptedAt;
    public DateTimeOffset? ListsRefreshedAt => long.TryParse(_settings.Get(SettingKeys.ListsRefreshedAt), out var t) ? TimeExt.FromUnix(t) : null;
    public string Version => _state.InstalledVersion;
    public string DataDir => _paths.DataDir;
    public string Fingerprint => _state.CertFingerprint;
    public string Host => System.Net.Dns.GetHostName();
    public IEnumerable<string> LanAddresses => Certificates.LanAddresses().Select(a => a.ToString());

    public void OnGet()
    {
        Values = Api.PublicSettings(_settings);
        Rules = _rules.All();
        ListCounts = _lists.Counts();
        Custom = _lists.Domains("custom");
        Overrides = _lists.Overrides();
    }
}

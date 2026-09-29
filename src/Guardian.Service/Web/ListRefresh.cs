using Guardian.Contracts;
using Guardian.Core.Storage;
using Guardian.Core.Time;

namespace Guardian.Service.Web;

/// <summary>Downloads each list in sources.txt when the parent clicks "Refresh lists". The only outbound HTTP the service ever makes.</summary>
public sealed class ListRefresh
{
    private readonly ListRepo _lists;
    private readonly SettingsRepo _settings;
    private readonly IHttpClientFactory _http;
    private readonly IClock _clock;
    private readonly ILogger<ListRefresh> _log;

    public ListRefresh(ListRepo lists, SettingsRepo settings, IHttpClientFactory http, IClock clock, ILogger<ListRefresh> log)
    {
        _lists = lists; _settings = settings; _http = http; _clock = clock; _log = log;
    }

    public IReadOnlyList<(string name, string url)> Sources()
    {
        var text = _settings.Get(SettingKeys.ListSources) ?? EmbeddedText("lists.sources.txt");
        return text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length == 2).Select(p => (p[0].ToLowerInvariant(), p[1].Trim())).ToList();
    }

    public async Task<Dictionary<string, string>> RefreshAsync(CancellationToken ct)
    {
        var results = new Dictionary<string, string>();
        var client = _http.CreateClient("lists");
        foreach (var (name, url) in Sources())
        {
            try
            {
                var text = await client.GetStringAsync(url, ct);
                var domains = ListLoader.ParseDomains(text).ToList();
                if (domains.Count == 0) { results[name] = "no domains found"; continue; }
                _lists.ReplaceList(name, domains, _clock.Now);
                results[name] = $"{domains.Count} domains";
            }
            catch (Exception ex) { results[name] = "failed: " + ex.Message; _log.LogWarning(ex, "List {Name} refresh failed", name); }
        }
        _settings.Set(SettingKeys.ListsRefreshedAt, _clock.Now.ToUnix().ToString());
        return results;
    }

    public static string EmbeddedText(string name)
    {
        using var s = typeof(ListRefresh).Assembly.GetManifestResourceStream(name);
        if (s is null) return "";
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}

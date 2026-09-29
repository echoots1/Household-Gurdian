using System.Windows.Automation;

namespace Guardian.Tray;

/// <summary>
/// Address-bar fallback used only while no browser extension is reporting. Reads the omnibox text of the
/// foreground browser through UI Automation and reduces it to the registrable domain. The path and query
/// are discarded here and never leave the process. Results are cached per window for 2 s because a UIA
/// descendant search is the most expensive thing the tray does.
/// </summary>
internal sealed class BrowserUrlReader
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(2);
    private static readonly string[] Browsers = { "chrome", "msedge", "brave", "firefox" };
    private static readonly string[] TitleSuffixes =
    {
        " - Google Chrome", " - Microsoft Edge", " - Brave", " — Mozilla Firefox", " - Mozilla Firefox",
        " - Personal - Microsoft Edge", " - Work - Microsoft Edge",
    };
    // Second-level labels under which the registrable domain is three labels long (bbc.co.uk, ox.ac.uk).
    private static readonly HashSet<string> SecondLevel = new(StringComparer.Ordinal) { "co", "com", "org", "net", "gov", "edu", "ac" };

    private IntPtr _cachedHwnd;
    private DateTime _cachedAt;
    private string? _cachedDomain;

    public static bool IsBrowser(string? processName) =>
        processName is not null && Browsers.Contains(processName.ToLowerInvariant());

    /// <summary>Returns (registrable domain, page title) for the browser window, or nulls when unavailable.</summary>
    public (string? Domain, string? Title) Read(IntPtr hwnd, string? windowTitle)
    {
        var domain = ReadDomain(hwnd);
        return (domain, domain is null ? null : StripBrowserSuffix(windowTitle));
    }

    private string? ReadDomain(IntPtr hwnd)
    {
        var now = DateTime.UtcNow;
        if (hwnd == _cachedHwnd && now - _cachedAt < CacheFor) return _cachedDomain;

        string? domain = null;
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            // The first Edit descendant of a Chromium/Firefox top-level window is the address bar.
            var edit = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            if (edit is not null && edit.TryGetCurrentPattern(ValuePattern.Pattern, out var p) && p is ValuePattern vp)
                domain = RegistrableDomain(vp.Current.Value);
        }
        catch
        {
            // UIA throws freely (element gone, window closing, COM errors). A missing domain is fine.
        }

        _cachedHwnd = hwnd;
        _cachedAt = now;
        _cachedDomain = domain;
        return domain;
    }

    /// <summary>
    /// "https://www.bbc.co.uk/news/uk?x=1" → "bbc.co.uk". Anything that does not look like a host
    /// (search text typed into the omnibox, chrome:// pages) yields null.
    /// </summary>
    internal static string? RegistrableDomain(string? addressBar)
    {
        if (string.IsNullOrWhiteSpace(addressBar)) return null;
        var s = addressBar.Trim();

        var schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            var scheme = s[..schemeEnd].ToLowerInvariant();
            if (scheme is not ("http" or "https")) return null;   // chrome://, edge://, file://, about:
            s = s[(schemeEnd + 3)..];
        }
        else if (s.Contains(':') && !s[..s.IndexOf(':')].Contains('.')) return null;   // about:blank, chrome:flags

        var slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        var at = s.LastIndexOf('@');
        if (at >= 0) s = s[(at + 1)..];                 // userinfo
        var colon = s.IndexOf(':');
        if (colon >= 0) s = s[..colon];                 // port
        s = s.ToLowerInvariant();

        if (s.Length == 0 || s.Contains(' ') || !s.Contains('.')) return null;
        if (s.All(c => char.IsDigit(c) || c == '.')) return s;     // IPv4 literal: keep as is

        var labels = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2) return null;
        var tld = labels[^1];
        var take = labels.Length >= 3 && tld.Length == 2 && SecondLevel.Contains(labels[^2]) ? 3 : 2;
        return string.Join('.', labels[^Math.Min(take, labels.Length)..]);
    }

    internal static string? StripBrowserSuffix(string? title)
    {
        if (string.IsNullOrEmpty(title)) return null;
        foreach (var suffix in TitleSuffixes)
            if (title.EndsWith(suffix, StringComparison.Ordinal))
                return title[..^suffix.Length];
        return title;
    }
}

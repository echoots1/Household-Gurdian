using Guardian.Contracts;
using Guardian.Core.Storage;

namespace Guardian.Core.Categorization;

/// <summary>Applies the ordered rule set. First match by priority wins; unmatched falls to Other.</summary>
public sealed class Categorizer
{
    private readonly IReadOnlyList<Rule> _rules;
    public int RulesVersion { get; }

    public Categorizer(IReadOnlyList<Rule> rules, int rulesVersion)
    {
        _rules = rules.OrderBy(r => r.Priority).ThenBy(r => r.Id).ToList();
        RulesVersion = rulesVersion;
    }

    public string Categorize(string? process, string? domain, string? title)
    {
        foreach (var r in _rules)
        {
            switch (r.MatchType)
            {
                case RuleMatch.Process:
                    if (process is not null && ProcessMatches(process, r.Pattern)) return r.Category;
                    break;
                case RuleMatch.Domain:
                    if (domain is not null && DomainMatches(domain, r.Pattern)) return r.Category;
                    break;
                case RuleMatch.TitleContains:
                    if (title is not null && title.Contains(r.Pattern, StringComparison.OrdinalIgnoreCase)) return r.Category;
                    break;
            }
        }
        return Categories.Other;
    }

    public string Categorize(ActivityEvent e) => Categorize(e.Process, e.Domain ?? e.Subdomain, e.Title);

    private static bool ProcessMatches(string process, string pattern)
    {
        var p = Strip(process);
        var q = Strip(pattern);
        if (q.EndsWith('*')) return p.StartsWith(q[..^1], StringComparison.OrdinalIgnoreCase);
        return string.Equals(p, q, StringComparison.OrdinalIgnoreCase);
    }

    private static string Strip(string s)
    {
        s = s.Trim();
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s;
    }

    /// <summary>A domain pattern matches the domain itself and any subdomain of it.</summary>
    public static bool DomainMatches(string domain, string pattern)
    {
        domain = domain.Trim().ToLowerInvariant();
        pattern = pattern.Trim().ToLowerInvariant().TrimStart('*').TrimStart('.');
        return domain == pattern || domain.EndsWith("." + pattern, StringComparison.Ordinal);
    }
}

public static class BrowserNames
{
    /// <summary>Process names (no extension) of browsers whose time is attributed to the active domain instead of the process.</summary>
    public static readonly HashSet<string> Processes = new(StringComparer.OrdinalIgnoreCase) { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "iexplore" };
    public static bool IsBrowser(string? process) => process is not null && Processes.Contains(process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process[..^4] : process);
}

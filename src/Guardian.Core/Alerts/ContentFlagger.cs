using Guardian.Contracts;
using Guardian.Core.Storage;

namespace Guardian.Core.Alerts;

/// <summary>On first sight of a domain, matches it against the category lists and writes a content_flag alert. Never blocks.</summary>
public sealed class ContentFlagger
{
    private readonly ListRepo _lists;
    private readonly AlertRepo _alerts;

    public ContentFlagger(ListRepo lists, AlertRepo alerts) { _lists = lists; _alerts = alerts; }

    /// <summary>Returns the list names that flagged, or empty.</summary>
    public IReadOnlyList<string> OnDomainSeen(string domain, string? title, int activeSeconds, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(domain)) return Array.Empty<string>();
        domain = domain.ToLowerInvariant();
        var first = _lists.MarkSeen(domain, at);
        if (!first) return Array.Empty<string>();
        return Check(domain, title, activeSeconds, at);
    }

    /// <summary>Evaluates a domain regardless of whether it was seen before (used when the parent marks "always flag").</summary>
    public IReadOnlyList<string> Check(string domain, string? title, int activeSeconds, DateTimeOffset at)
    {
        var ov = _lists.Override(domain);
        if (ov == "allow") return Array.Empty<string>();
        var lists = _lists.Match(domain).ToList();
        if (ov == "flag" && lists.Count == 0) lists.Add("always-flag");
        if (lists.Count == 0) return lists;
        _alerts.Add(AlertType.ContentFlag, new { domain, category = string.Join(", ", lists), title, firstSeen = at, activeSeconds }, at);
        return lists;
    }
}

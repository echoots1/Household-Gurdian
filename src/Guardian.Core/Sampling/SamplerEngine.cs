using Guardian.Contracts;
using Guardian.Core.Categorization;
using Guardian.Core.Storage;
using Guardian.Core.Time;

namespace Guardian.Core.Sampling;

/// <summary>
/// Turns 5-second foreground samples and browser tab reports into activity_event intervals.
/// Rules: idle (≥ IdleThreshold), locked, or no session → an idle interval that counts toward nothing.
/// When the foreground process is a browser, the browser's own process is never charged; the
/// active domain (from the extension, or the tray's address-bar fallback) is charged instead.
/// </summary>
public sealed class SamplerEngine
{
    public const int SampleSeconds = 5;
    public const int IdleThresholdSeconds = 120;
    /// <summary>A gap longer than this between samples closes the open interval (PC asleep, tray gone).</summary>
    public const int MaxGapSeconds = 20;

    private readonly ActivityRepo _activity;
    private readonly Func<Categorizer> _categorizer;
    private readonly Func<string[]> _titleCategories;
    private readonly object _lock = new();

    // The one open interval, if any.
    private long _openId = -1;
    private string _openKey = "";
    private DateTimeOffset _openEnd;
    private DateTimeOffset? _lastSampleAt;

    // Latest domain the extension reported, and when.
    private string? _extDomain, _extSubdomain, _extTitle, _extBrowser;
    private DateTimeOffset _extAt = DateTimeOffset.MinValue;

    public string? LastProcess { get; private set; }
    public string? LastDomain { get; private set; }
    public string? LastCategory { get; private set; }
    public bool LastIdle { get; private set; } = true;
    public DateTimeOffset LastExtensionReport => _extAt;

    public SamplerEngine(ActivityRepo activity, Func<Categorizer> categorizer, Func<string[]> titleCategories)
    {
        _activity = activity; _categorizer = categorizer; _titleCategories = titleCategories;
    }

    public bool ExtensionActive(DateTimeOffset now) => (now - _extAt).TotalSeconds < 120;

    /// <summary>Extension → service. The extension reports an interval that just ended; we remember its domain as "current" for the next samples.</summary>
    public void OnTabReport(TabReport r, DateTimeOffset now)
    {
        lock (_lock)
        {
            _extAt = now;
            if (string.IsNullOrWhiteSpace(r.Domain)) { _extDomain = null; return; }
            _extDomain = r.Domain.Trim().ToLowerInvariant();
            _extSubdomain = r.Subdomain?.Trim().ToLowerInvariant();
            _extTitle = r.Title;
            _extBrowser = r.Browser;
        }
    }

    public void OnSample(Sample s)
    {
        lock (_lock)
        {
            var now = s.At;
            if (_lastSampleAt is { } last && (now - last).TotalSeconds > MaxGapSeconds) CloseOpen();
            _lastSampleAt = now;

            var idle = s.Locked || s.IdleSeconds >= IdleThresholdSeconds || string.IsNullOrEmpty(s.Process);
            string kind, key, category;
            string? process = s.Process, title = s.Title, domain = null, subdomain = null, browser = null;

            if (idle)
            {
                kind = "idle"; key = "idle"; category = Categories.Other; process = null; title = null;
            }
            else if (BrowserNames.IsBrowser(s.Process))
            {
                // Prefer the extension's domain if it reported recently; else the tray's address-bar reading; else the browser process with no domain.
                if (ExtensionActive(now) && _extDomain is not null) { domain = _extDomain; subdomain = _extSubdomain; title = _extTitle ?? title; browser = _extBrowser; }
                else if (!string.IsNullOrEmpty(s.BrowserDomain)) { domain = s.BrowserDomain.ToLowerInvariant(); subdomain = domain; title = s.BrowserTitle ?? title; browser = "tray"; }
                if (domain is not null) { kind = "web"; key = "web:" + domain; }
                else { kind = "app"; key = "app:" + process!.ToLowerInvariant(); }
                category = _categorizer().Categorize(process, domain, title);
            }
            else
            {
                kind = "app"; key = "app:" + process!.ToLowerInvariant();
                category = _categorizer().Categorize(process, null, title);
            }

            if (!idle && title is not null && !_titleCategories().Contains(category, StringComparer.OrdinalIgnoreCase)) title = null;
            key += "|" + category + "|" + (title ?? "");

            if (_openId >= 0 && _openKey == key)
            {
                _openEnd = now;
                _activity.Extend(_openId, now);
            }
            else
            {
                CloseOpen();
                var start = now.AddSeconds(-SampleSeconds);
                _openId = _activity.Insert(start, now, kind, process, title, domain, subdomain, idle, browser, category);
                _openKey = key; _openEnd = now;
            }
            LastProcess = process; LastDomain = domain; LastCategory = idle ? null : category; LastIdle = idle;
        }
    }

    /// <summary>No sample arrived (session gone): close whatever is open.</summary>
    public void OnNoSession()
    {
        lock (_lock) CloseOpen();
    }

    private void CloseOpen()
    {
        _openId = -1; _openKey = "";
        LastIdle = true; LastCategory = null;
    }
}

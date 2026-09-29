using System.Net;
using Guardian.Contracts;
using Guardian.Core.Storage;
using Guardian.Service.Infrastructure;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Guardian.Service.Pages.Me;

public class CollectedModel : PageModel
{
    private readonly SettingsRepo _settings; private readonly GuardianState _state;
    public CollectedModel(SettingsRepo settings, GuardianState state) { _settings = settings; _state = state; }
    public string NoticeHtml { get; private set; } = "";
    public DateTimeOffset? AcceptedAt => _settings.NoticeAcceptedAt;
    public int RawDays => _settings.RetentionRawDays; public int ItemDays => _settings.RetentionItemDays; public int AlertDays => _settings.RetentionAlertDays;
    public string[] TitleCategories => _settings.TitleCategories;

    public void OnGet() => NoticeHtml = MarkdownLite(NoticeText.Render(_settings.MonitoredUser, _state.Status.ChildUrl));

    /// <summary>Just enough markdown for notice-text.md: headings, bullets, bold. Everything is HTML-encoded first.</summary>
    public static string MarkdownLite(string md)
    {
        var sb = new System.Text.StringBuilder(); var inList = false;
        foreach (var raw in md.Split('\n'))
        {
            var line = WebUtility.HtmlEncode(raw.TrimEnd('\r'));
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\*\*(.+?)\*\*", "<b>$1</b>");
            if (line.StartsWith("- ")) { if (!inList) { sb.Append("<ul>"); inList = true; } sb.Append("<li>").Append(line[2..]).Append("</li>"); continue; }
            if (inList) { sb.Append("</ul>"); inList = false; }
            if (line.StartsWith("# ")) sb.Append("<h1>").Append(line[2..]).Append("</h1>");
            else if (line.StartsWith("## ")) sb.Append("<h3>").Append(line[3..]).Append("</h3>");
            else if (line.Length > 0) sb.Append("<p>").Append(line).Append("</p>");
        }
        if (inList) sb.Append("</ul>");
        return sb.ToString();
    }
}

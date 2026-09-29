using System.Reflection;

namespace Guardian.Contracts;

/// <summary>The notice the child reads at setup. One source (docs/notice-text.md), shown by the tray and the web UI alike.</summary>
public static class NoticeText
{
    private static readonly Lazy<string> _markdown = new(() =>
    {
        using var s = typeof(NoticeText).Assembly.GetManifestResourceStream("Guardian.Contracts.notice-text.md")
            ?? throw new InvalidOperationException("notice-text.md is not embedded");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    });

    public static string Markdown => _markdown.Value;

    /// <summary>Fills the placeholders the notice text uses.</summary>
    public static string Render(string monitoredUser, string childUrl) =>
        Markdown.Replace("{account}", string.IsNullOrEmpty(monitoredUser) ? "(not set)" : monitoredUser)
                .Replace("{url}", childUrl);

    /// <summary>Very small markdown → plain text for the tray dialog.</summary>
    public static string ToPlainText(string md)
    {
        var lines = md.Split('\n').Select(l => l.TrimEnd('\r'))
            .Select(l => l.StartsWith("# ") ? l[2..].ToUpperInvariant() : l)
            .Select(l => l.StartsWith("## ") ? l[3..].ToUpperInvariant() : l)
            .Select(l => l.StartsWith("- ") ? "  • " + l[2..] : l)
            .Select(l => l.Replace("**", ""));
        return string.Join('\n', lines);
    }
}

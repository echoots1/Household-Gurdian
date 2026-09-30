using Guardian.Contracts;
using Guardian.Core.Storage;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Guardian.Core.Alerts;

public sealed record SmtpSettings(string Host, int Port, string? User, string? Password, string From, string To, bool UseTls)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(To) && !string.IsNullOrWhiteSpace(From);
}

public interface IEmailSender
{
    Task SendAsync(SmtpSettings s, string subject, string body, CancellationToken ct);
}

public sealed class MailKitSender : IEmailSender
{
    public async Task SendAsync(SmtpSettings s, string subject, string body, CancellationToken ct)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(s.From));
        foreach (var to in s.To.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) msg.To.Add(MailboxAddress.Parse(to));
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = body };
        using var client = new SmtpClient();
        await client.ConnectAsync(s.Host, s.Port, s.UseTls ? SecureSocketOptions.StartTlsWhenAvailable : SecureSocketOptions.Auto, ct);
        if (!string.IsNullOrEmpty(s.User)) await client.AuthenticateAsync(s.User, s.Password ?? "", ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
    }
}

/// <summary>Batches un-emailed alerts into one message every 5 minutes so a burst of flags is one email.</summary>
public sealed class EmailDigest
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private readonly AlertRepo _alerts;
    private readonly IEmailSender _sender;
    private readonly Func<SmtpSettings?> _settings;

    public EmailDigest(AlertRepo alerts, IEmailSender sender, Func<SmtpSettings?> settings)
    {
        _alerts = alerts; _sender = sender; _settings = settings;
    }

    /// <summary>Sends the pending digest if SMTP is configured. Returns the number of alerts included.</summary>
    public async Task<int> FlushAsync(CancellationToken ct)
    {
        var s = _settings();
        var pending = _alerts.TakeUnemailed();
        if (pending.Count == 0) return 0;
        if (s is null || !s.IsConfigured) { _alerts.MarkEmailed(pending.Select(a => a.Id)); return 0; }
        var body = Format(pending);
        await _sender.SendAsync(s, $"[Guardian] {pending.Count} new alert{(pending.Count == 1 ? "" : "s")}", body, ct);
        _alerts.MarkEmailed(pending.Select(a => a.Id));
        return pending.Count;
    }

    public static string Format(IReadOnlyList<Alert> alerts)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Guardian alerts");
        sb.AppendLine();
        foreach (var a in alerts)
        {
            sb.Append(a.CreatedAt.ToString("yyyy-MM-dd HH:mm")).Append("  ");
            sb.AppendLine(a.Type switch
            {
                AlertType.ContentFlag => $"Content flag: {a.Str("domain")} ({a.Str("category")})",
                AlertType.TimeRequest => $"Time request: {a.Str("reason") ?? "(no reason given)"}",
                AlertType.TrayRelaunch => "Tray was relaunched",
                AlertType.ClockJump => "System clock jumped",
                AlertType.BackupFailed => $"Backup failed: {a.Str("error")}",
                AlertType.ExtensionMissing => "Browser extension not reporting; using address-bar fallback",
                _ => a.Type,
            });
        }
        sb.AppendLine();
        sb.AppendLine("Open the Guardian dashboard on the child's PC to acknowledge these.");
        return sb.ToString();
    }
}

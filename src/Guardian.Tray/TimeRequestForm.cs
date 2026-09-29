using System.Net.Http;
using System.Net.Http.Json;
using Guardian.Contracts;

namespace Guardian.Tray;

/// <summary>
/// "Request more time…": an optional one-line reason, posted to the service's loopback listener.
/// The parent sees it as a time_request alert; nothing changes on this machine until they act.
/// </summary>
internal sealed class TimeRequestForm : Form
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly Uri Endpoint = new($"http://127.0.0.1:{Ports.Local}/me/time-request");

    private readonly TextBox _reason;
    private readonly Button _send;
    private readonly Label _result;

    public TimeRequestForm()
    {
        Text = "Request more time";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 150);
        Font = new Font("Segoe UI", 10f);

        var prompt = new Label { Text = "Why do you need more time? (optional, one line)", AutoSize = true, Location = new Point(12, 12) };
        _reason = new TextBox { Location = new Point(12, 38), Width = 396, MaxLength = 200 };
        _send = new Button { Text = "Send request", Location = new Point(288, 72), Width = 120, Height = 30 };
        _result = new Label { AutoSize = false, Location = new Point(12, 112), Width = 396, Height = 28 };

        Controls.AddRange(new Control[] { prompt, _reason, _send, _result });
        AcceptButton = _send;
        _send.Click += async (_, _) => await SendAsync();
    }

    private async Task SendAsync()
    {
        _send.Enabled = false;
        _result.Text = "Sending…";
        try
        {
            using var response = await Http.PostAsJsonAsync(Endpoint, new { reason = _reason.Text.Trim() });
            if (response.IsSuccessStatusCode)
            {
                _result.Text = "Sent. Your parent will see the request.";
                _reason.ReadOnly = true;
            }
            else
            {
                _result.Text = $"Not sent: the service answered {(int)response.StatusCode} {response.ReasonPhrase}.";
                _send.Enabled = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Time request failed", ex);
            _result.Text = "Not sent: Guardian isn't reachable right now.";
            _send.Enabled = true;
        }
    }
}

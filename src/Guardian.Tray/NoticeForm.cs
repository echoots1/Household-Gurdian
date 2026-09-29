using Guardian.Contracts;

namespace Guardian.Tray;

/// <summary>
/// The small always-on-top notice dialog: title, one sentence, one OK button. A non-dismissible notice
/// (the 1-minute countdown) has no close box, a disabled OK, and closes itself when the countdown ends.
/// This is the only non-dismissible UI in the product.
/// </summary>
internal sealed class NoticeForm : Form
{
    private readonly TrayNotice _notice;
    private readonly Action<string> _onDismissed;
    private readonly Label _countdown;
    private readonly Button _ok;
    private bool _userDismissed;
    private bool _autoClosing;

    public string NoticeId => _notice.Id;

    public NoticeForm(TrayNotice notice, Action<string> onDismissed)
    {
        _notice = notice;
        _onDismissed = onDismissed;

        Text = string.IsNullOrWhiteSpace(notice.Title) ? Names.Product : notice.Title;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = notice.Dismissible;
        ClientSize = new Size(440, 150);
        Font = new Font("Segoe UI", 10f);

        var message = new Label
        {
            Text = notice.Message,
            Location = new Point(16, 16),
            Size = new Size(408, 70),
            AutoSize = false,
        };
        _countdown = new Label
        {
            Location = new Point(16, 96),
            Size = new Size(200, 40),
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Visible = notice.CountdownTo is not null,
        };
        _ok = new Button
        {
            Text = "OK",
            Location = new Point(324, 104),
            Size = new Size(100, 32),
            Enabled = notice.Dismissible,
        };
        _ok.Click += (_, _) => { _userDismissed = true; Close(); };

        Controls.AddRange(new Control[] { message, _countdown, _ok });
        if (notice.Dismissible) { AcceptButton = _ok; CancelButton = _ok; }

        FormClosing += OnClosing;
        FormClosed += (_, _) => { if (_userDismissed) _onDismissed(_notice.Id); };
        Tick();
    }

    /// <summary>Called once a second by the application context; updates the countdown label.</summary>
    public void Tick()
    {
        if (_notice.CountdownTo is not { } to) return;
        var left = to - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero)
        {
            _countdown.Text = "0:00";
            if (!_notice.Dismissible && !_autoClosing)
            {
                _autoClosing = true;
                Close();
            }
            return;
        }
        _countdown.Text = $"{(int)left.TotalMinutes}:{left.Seconds:00}";
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_notice.Dismissible)
        {
            // Closing via the title bar or Alt+F4 is still the user dismissing the notice.
            if (e.CloseReason == CloseReason.UserClosing) _userDismissed = true;
            return;
        }
        // Non-dismissible: only the countdown (or the app itself) may close it.
        if (e.CloseReason == CloseReason.UserClosing && !_autoClosing) e.Cancel = true;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_ok.Enabled) _ok.Focus();
    }
}

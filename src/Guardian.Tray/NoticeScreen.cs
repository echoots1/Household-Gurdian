namespace Guardian.Tray;

/// <summary>
/// The full-screen notice-acknowledgment screen shown until the service reports NoticeAccepted.
/// It is borderless, maximized and topmost, but it is not a lock: the child can Alt-Tab away or close
/// it, and the application context simply shows it again on the next status poll. Overt, not covert.
/// </summary>
internal sealed class NoticeScreen : Form
{
    private readonly TextBox _text;
    private readonly Action _onAccept;

    public NoticeScreen(string text, Action onAccept)
    {
        _onAccept = onAccept;

        Text = "Guardian notice";
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        TopMost = true;
        ShowInTaskbar = true;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 14f);

        var heading = new Label
        {
            Text = "Please read this before you continue",
            Dock = DockStyle.Top,
            Height = 70,
            Font = new Font("Segoe UI", 22f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(40, 0, 40, 0),
        };
        _text = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            Font = new Font("Segoe UI", 16f),
            WordWrap = true,
        };
        var textHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(40, 10, 40, 10), BackColor = Color.White };
        textHost.Controls.Add(_text);

        var accept = new Button
        {
            Text = "I understand",
            Size = new Size(260, 56),
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
        };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 90, Padding = new Padding(40, 16, 40, 16) };
        bottom.Controls.Add(accept);
        bottom.Resize += (_, _) => accept.Location = new Point(bottom.ClientSize.Width - 40 - accept.Width, 16);
        accept.Location = new Point(bottom.ClientSize.Width - 40 - accept.Width, 16);

        Controls.Add(textHost);
        Controls.Add(bottom);
        Controls.Add(heading);
        AcceptButton = accept;

        accept.Click += (_, _) =>
        {
            accept.Enabled = false;
            accept.Text = "Thank you";
            _onAccept();
        };
        SetText(text);
        Shown += (_, _) => { _text.SelectionStart = 0; _text.SelectionLength = 0; accept.Focus(); };
    }

    public void SetText(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        if (_text.Text != normalized) _text.Text = normalized;
    }
}

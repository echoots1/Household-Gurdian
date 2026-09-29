namespace Guardian.Tray;

/// <summary>"What Guardian collects": the notice text, read-only.</summary>
internal sealed class CollectsForm : Form
{
    public CollectsForm(string text)
    {
        Text = "What Guardian collects";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(640, 560);
        MinimumSize = new Size(400, 300);
        ShowInTaskbar = true;

        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10f),
            Text = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            BackColor = SystemColors.Window,
            WordWrap = true,
        };
        var close = new Button { Text = "Close", DialogResult = DialogResult.OK, Dock = DockStyle.Right, Width = 100 };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
        bottom.Controls.Add(close);

        Controls.Add(box);
        Controls.Add(bottom);
        AcceptButton = close;
        CancelButton = close;
        close.Click += (_, _) => Close();
        Shown += (_, _) => { box.SelectionStart = 0; box.SelectionLength = 0; close.Focus(); };
    }
}

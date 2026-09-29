using System.Drawing.Drawing2D;
using Guardian.Contracts;

namespace Guardian.Tray;

/// <summary>Draws the five shield icons at runtime with GDI+, so the build needs no .ico asset.</summary>
internal static class IconFactory
{
    private static readonly Color Neutral = Color.FromArgb(0x5B, 0x72, 0x8A);   // blue-grey
    private static readonly Color Amber = Color.FromArgb(0xE0, 0x9F, 0x1F);
    private static readonly Color Red = Color.FromArgb(0xC6, 0x2E, 0x2E);
    private static readonly Color Grey = Color.FromArgb(0x9A, 0x9A, 0x9A);

    public static Icon Create(TrayState state, int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var shield = ShieldPath(size);
            switch (state)
            {
                case TrayState.On:
                    Fill(g, shield, Neutral);
                    break;
                case TrayState.Warning:
                    Fill(g, shield, Amber);
                    break;
                case TrayState.Enforcing:
                    Fill(g, shield, Red);
                    break;
                case TrayState.WaitingForNotice:
                    using (var pen = new Pen(Neutral, Math.Max(2f, size / 10f)) { LineJoin = LineJoin.Round })
                        g.DrawPath(pen, shield);
                    break;
                case TrayState.ServiceDown:
                    Fill(g, shield, Grey);
                    using (var pen = new Pen(Color.FromArgb(0x30, 0x30, 0x30), Math.Max(2f, size / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLine(pen, size * 0.2f, size * 0.8f, size * 0.8f, size * 0.2f);
                    break;
            }
        }

        // GetHicon allocates an unmanaged HICON that Icon.FromHandle does not own; clone into a managed icon
        // and destroy the handle so nothing leaks (the icons are created once per state and cached).
        var hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            try { NativeMethods.DestroyIcon(hIcon); } catch { }
        }
    }

    private static void Fill(Graphics g, GraphicsPath shield, Color color)
    {
        using var brush = new SolidBrush(color);
        g.FillPath(brush, shield);
        using var pen = new Pen(Color.FromArgb(0x60, Color.Black), 1f);
        g.DrawPath(pen, shield);
    }

    /// <summary>A classic heater shield: flat top, straight sides, pointed bottom.</summary>
    private static GraphicsPath ShieldPath(int s)
    {
        float m = s * 0.12f;           // margin
        float w = s - 2 * m;
        var path = new GraphicsPath();
        path.AddLine(m, m, m + w, m);                        // top edge
        path.AddLine(m + w, m, m + w, m + w * 0.55f);         // right side
        path.AddBezier(m + w, m + w * 0.55f, m + w, m + w * 0.95f, m + w * 0.65f, s - m - w * 0.1f, s / 2f, s - m);   // right curve to tip
        path.AddBezier(s / 2f, s - m, m + w * 0.35f, s - m - w * 0.1f, m, m + w * 0.95f, m, m + w * 0.55f);           // left curve
        path.CloseFigure();
        return path;
    }
}

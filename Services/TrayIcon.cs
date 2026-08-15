using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace SoundFluent.Services;

/// <summary>
/// Tray presence and its menu. The icon is drawn at runtime so the project
/// carries no binary assets.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _drawn;

    public event Action? OpenRequested;
    public event Action? ApiKeyRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _drawn = Draw();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open  (Ctrl+Alt+P)", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Set API key…", null, (_, _) => ApiKeyRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Sound Fluent", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = _drawn,
            Text = "Sound Fluent — Ctrl+Alt+P",
            Visible = true,
            ContextMenuStrip = menu
        };

        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    private static Icon Draw()
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var disc = new SolidBrush(Color.FromArgb(0x1D, 0x5F, 0xA8));
            g.FillEllipse(disc, 0, 0, 31, 31);

            using var font = new Font("Cambria", 19, FontStyle.Bold, GraphicsUnit.Pixel);
            using var ink = new SolidBrush(Color.White);
            var centre = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString("ą", font, ink, new RectangleF(0, -1, 32, 32), centre);
        }

        IntPtr handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _drawn.Dispose();
    }
}

using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Notch.Services;

public sealed record TrayActions(
    Func<bool> IsDoNotDisturb,
    Action ToggleDoNotDisturb,
    Func<bool> IsTimerRunning,
    Action ToggleTimer,
    Action ToggleIsland,
    Action TestEvent,
    Action OpenLog,
    Func<bool> IsGmailConnected,
    Action GmailConnect,
    Action GmailDisconnect,
    Func<bool> IsNotionConnected,
    Action NotionConnect,
    Action NotionDisconnect,
    Action Exit);

/// <summary>Tray-Icon mit Menü. Linksklick = Insel/Quick Actions wie der Hotkey.</summary>
public sealed class TrayService : IDisposable
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon _image;

    public TrayService(TrayActions actions)
    {
        _image = CreateIcon();

        var dnd = new Forms.ToolStripMenuItem("Nicht stören");
        var timer = new Forms.ToolStripMenuItem("Fokus-Timer starten");
        var autostart = new Forms.ToolStripMenuItem("Mit Windows starten");
        var gmail = new Forms.ToolStripMenuItem("Gmail verbinden …");
        var gmailOff = new Forms.ToolStripMenuItem("Gmail trennen");
        var notion = new Forms.ToolStripMenuItem("Notion verbinden …");
        var notionOff = new Forms.ToolStripMenuItem("Notion trennen");

        dnd.Click += (_, _) => actions.ToggleDoNotDisturb();
        timer.Click += (_, _) => actions.ToggleTimer();
        autostart.Click += (_, _) => Autostart.Set(!Autostart.IsEnabled());
        gmail.Click += (_, _) => actions.GmailConnect();
        gmailOff.Click += (_, _) => actions.GmailDisconnect();
        notion.Click += (_, _) => actions.NotionConnect();
        notionOff.Click += (_, _) => actions.NotionDisconnect();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(dnd);
        menu.Items.Add(timer);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Test-Event", null, (_, _) => actions.TestEvent());
        menu.Items.Add("Log öffnen", null, (_, _) => actions.OpenLog());
        menu.Items.Add(gmail);
        menu.Items.Add(gmailOff);
        menu.Items.Add(notion);
        menu.Items.Add(notionOff);
        menu.Items.Add(autostart);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => actions.Exit());

        // Häkchen/Texte beim Öffnen aktualisieren
        menu.Opening += (_, _) =>
        {
            dnd.Checked = actions.IsDoNotDisturb();
            timer.Text = actions.IsTimerRunning() ? "Fokus-Timer stoppen" : "Fokus-Timer starten";
            autostart.Checked = Autostart.IsEnabled();
            var connected = actions.IsGmailConnected();
            gmail.Text = connected ? "Gmail neu verbinden …" : "Gmail verbinden …";
            gmailOff.Visible = connected;
            var notionOn = actions.IsNotionConnected();
            notion.Text = notionOn ? "Notion neu verbinden …" : "Notion verbinden …";
            notionOff.Visible = notionOn;
        };

        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = "Notch",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) actions.ToggleIsland();
        };
    }

    /// <summary>Zeichnet eine kleine Pill als Icon (hell mit dunklem Rand, auf hellem und dunklem Taskbar lesbar).</summary>
    private static Drawing.Icon CreateIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var path = new Drawing.Drawing2D.GraphicsPath();
            var r = new Drawing.RectangleF(3, 10, 26, 12);
            const float d = 12f;
            path.AddArc(r.X, r.Y, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            path.CloseFigure();
            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 20, 20, 20));
            using var pen = new Drawing.Pen(Drawing.Color.FromArgb(220, 255, 255, 255), 1.6f);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
        var handle = bmp.GetHicon();
        try { return (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}

using System.Drawing;
using System.Windows.Forms;
using System.Windows.Threading;
using HADA.Tray.Localization;

namespace HADA.Tray;

/// <summary>Notification-area icon showing whether the service is reachable, with Open and Exit commands.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Font _boldFont;
    private readonly DispatcherTimer _statusTimer;

    public TrayIcon(Func<bool> isConnected, Action open, Action exit)
    {
        _menu = new ContextMenuStrip();
        _boldFont = new Font(_menu.Font, System.Drawing.FontStyle.Bold);
        _menu.Items.Add(new ToolStripMenuItem(Loc.Get("Tray_Open"), image: null, (_, _) => open()) { Font = _boldFont });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Loc.Get("Tray_Exit"), image: null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = Loc.Get("Tray_Waiting"),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => _icon.Text = Loc.Get(isConnected() ? "Tray_Connected" : "Tray_Waiting");
        _statusTimer.Start();
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _boldFont.Dispose();
    }
}

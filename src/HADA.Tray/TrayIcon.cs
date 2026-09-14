using System.Drawing;
using System.Windows.Forms;
using System.Windows.Threading;

namespace HADA.Tray;

/// <summary>Notification-area icon showing whether the service is reachable, with an Exit command.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const string Connected = "HADA: connected to service";
    private const string Waiting = "HADA: waiting for service";

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly DispatcherTimer _statusTimer;

    public TrayIcon(Func<bool> isConnected, Action exit)
    {
        _menu = new ContextMenuStrip();
        _menu.Items.Add("Exit", image: null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = Waiting,
            ContextMenuStrip = _menu,
            Visible = true,
        };

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => _icon.Text = isConnected() ? Connected : Waiting;
        _statusTimer.Start();
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}

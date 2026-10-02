using System.Drawing;
using System.Windows.Forms;
using System.Windows.Threading;
using HADA.Tray.Localization;
using Microsoft.Win32;

namespace HADA.Tray;

internal static class AppIcon
{
    /// <summary>The application icon embedded in this assembly; named in full so it also resolves when hosted by another executable.</summary>
    public static Uri Uri { get; } = new("pack://application:,,,/HADA.Tray;component/Assets/hada.ico");
}

/// <summary>
/// Notification-area icon showing whether the service is reachable. Its menu opens the window and, when they are
/// set up, the dashboard window and the quick actions.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Font _boldFont;
    private readonly Icon _appIcon;
    private readonly DispatcherTimer _statusTimer;
    private readonly Action _open;
    private readonly Action _openDashboard;
    private readonly Action _exit;
    private IReadOnlyList<(string Name, string Shortcut, Action Chosen)> _quickActions = [];

    /// <param name="openDashboard">Opens the dashboard window; offered only while an address for it is set.</param>
    public TrayIcon(Func<bool> isConnected, Action open, Action openDashboard, Action exit)
    {
        _open = open;
        _openDashboard = openDashboard;
        _exit = exit;
        _menu = new ContextMenuStrip { ShowImageMargin = false };
        _boldFont = new Font(_menu.Font, System.Drawing.FontStyle.Bold);

        // Built each time it opens: the dashboard address is a setting another process may have changed, and
        // Windows Forms menus know nothing about dark mode, so the colours are those of the theme at that moment.
        _menu.Opening += (_, _) =>
        {
            BuildMenu();
            ApplyTheme(MenuTheme.ForSystem());
        };
        BuildMenu();

        using (var resource = System.Windows.Application.GetResourceStream(AppIcon.Uri)!.Stream)
        {
            _appIcon = new Icon(resource, SystemInformation.SmallIconSize);
        }

        _icon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = Loc.Get("Tray_Waiting"),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
        _icon.BalloonTipClicked += (_, _) => open();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => _icon.Text = Loc.Get(isConnected() ? "Tray_Connected" : "Tray_Waiting");
        _statusTimer.Start();
    }

    /// <summary>The quick actions to offer at the top of the menu, each with its keyboard shortcut if it has one.</summary>
    public void SetQuickActions(IReadOnlyList<(string Name, string Shortcut, Action Chosen)> actions) => _quickActions = actions;

    private void BuildMenu()
    {
        _menu.Items.Clear();
        foreach (var (name, shortcut, chosen) in _quickActions)
        {
            _menu.Items.Add(new ToolStripMenuItem(name, image: null, (_, _) => chosen())
            {
                // Shown at the right edge, as menus show shortcuts; the shortcut itself is registered elsewhere.
                ShortcutKeyDisplayString = shortcut.Length > 0 ? shortcut : null,
            });
        }

        if (_quickActions.Count > 0)
        {
            _menu.Items.Add(new ToolStripSeparator());
        }

        _menu.Items.Add(new ToolStripMenuItem(Loc.Get("Tray_Open"), image: null, (_, _) => _open()) { Font = _boldFont });
        if (UserPreferences.DashboardUrl.Length > 0)
        {
            _menu.Items.Add(Loc.Get("Tray_Dashboard"), image: null, (_, _) => _openDashboard());
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Loc.Get("Tray_Exit"), image: null, (_, _) => _exit());
    }

    /// <summary>
    /// Shows a Windows notification coming from this icon. Windows cuts titles and texts it finds too long,
    /// so both are shortened here, where an ellipsis can say so.
    /// </summary>
    public void ShowNotification(string title, string message)
    {
        const int MaxTitleLength = 63;
        const int MaxMessageLength = 255;
        const int Timeout = 10_000;

        if (!string.IsNullOrWhiteSpace(message))
        {
            _icon.ShowBalloonTip(Timeout, Shorten(title.Trim(), MaxTitleLength), Shorten(message.Trim(), MaxMessageLength), ToolTipIcon.None);
        }
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon.Dispose();
        _menu.Dispose();
        _boldFont.Dispose();
    }

    private static string Shorten(string text, int maxLength) =>
        text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength - 1), "…");

    private void ApplyTheme(MenuTheme theme)
    {
        _menu.Renderer = new ToolStripProfessionalRenderer(theme) { RoundedEdges = false };
        _menu.BackColor = theme.Background;
        _menu.ForeColor = theme.Text;
        foreach (ToolStripItem item in _menu.Items)
        {
            item.ForeColor = theme.Text;
        }
    }

    /// <summary>Menu colours matching Windows 11's own light and dark context menus.</summary>
    private sealed class MenuTheme(Color background, Color hover, Color border, Color text) : ProfessionalColorTable
    {
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        public Color Background => background;

        public Color Text => text;

        public override Color ToolStripDropDownBackground => background;

        public override Color ImageMarginGradientBegin => background;

        public override Color ImageMarginGradientMiddle => background;

        public override Color ImageMarginGradientEnd => background;

        public override Color MenuItemSelected => hover;

        public override Color MenuItemSelectedGradientBegin => hover;

        public override Color MenuItemSelectedGradientEnd => hover;

        public override Color MenuItemPressedGradientBegin => hover;

        public override Color MenuItemPressedGradientEnd => hover;

        public override Color MenuItemBorder => hover;

        public override Color MenuBorder => border;

        public override Color SeparatorDark => border;

        public override Color SeparatorLight => background;

        public static MenuTheme ForSystem() => AppsUseDarkTheme()
            ? new MenuTheme(Color.FromArgb(0x2C, 0x2C, 0x2C), Color.FromArgb(0x3D, 0x3D, 0x3D), Color.FromArgb(0x4A, 0x4A, 0x4A), Color.White)
            : new MenuTheme(Color.FromArgb(0xF9, 0xF9, 0xF9), Color.FromArgb(0xE8, 0xE8, 0xE8), Color.FromArgb(0xD0, 0xD0, 0xD0), Color.Black);

        /// <summary>The "Choose your mode" setting for apps; light when it cannot be read.</summary>
        private static bool AppsUseDarkTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue("AppsUseLightTheme") is int useLight && useLight == 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
            {
                return false;
            }
        }
    }
}

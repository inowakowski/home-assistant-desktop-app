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

/// <summary>Notification-area icon showing whether the service is reachable, with Open and Exit commands.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Font _boldFont;
    private readonly Icon _appIcon;
    private readonly DispatcherTimer _statusTimer;

    public TrayIcon(Func<bool> isConnected, Action open, Action exit)
    {
        _menu = new ContextMenuStrip { ShowImageMargin = false };
        _boldFont = new Font(_menu.Font, System.Drawing.FontStyle.Bold);
        _menu.Items.Add(new ToolStripMenuItem(Loc.Get("Tray_Open"), image: null, (_, _) => open()) { Font = _boldFont });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Loc.Get("Tray_Exit"), image: null, (_, _) => exit());

        // Windows Forms menus know nothing about dark mode; colour the menu each time it opens, so it follows the
        // system theme even when that changes while the tray is running.
        _menu.Opening += (_, _) => ApplyTheme(MenuTheme.ForSystem());

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

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => _icon.Text = Loc.Get(isConnected() ? "Tray_Connected" : "Tray_Waiting");
        _statusTimer.Start();
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

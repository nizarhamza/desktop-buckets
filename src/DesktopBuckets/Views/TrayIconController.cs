using System;
using System.Drawing;
using System.IO;
using WinForms = System.Windows.Forms;

namespace DesktopBuckets.Views
{
    /// <summary>System-tray presence. Thin wrapper over WinForms <see cref="WinForms.NotifyIcon"/>.</summary>
    public sealed class TrayIconController : IDisposable
    {
        private readonly WinForms.NotifyIcon _icon;
        private readonly WinForms.ToolStripMenuItem _shellItem;

        public event Action? NewBucketRequested;
        public event Action? ShowAllRequested;
        public event Action? SettingsRequested;
        public event Action<bool>? ToggleShellRequested;
        public event Action? OpenFolderRequested;
        public event Action? RealignIconsRequested;
        public event Action? CheckUpdatesRequested;
        public event Action? QuitRequested;

        public TrayIconController(bool shellEnabled, string versionLabel)
        {
            var menu = new WinForms.ContextMenuStrip();

            var header = new WinForms.ToolStripMenuItem($"Desktop Buckets {versionLabel}") { Enabled = false };
            menu.Items.Add(header);
            menu.Items.Add(new WinForms.ToolStripSeparator());

            menu.Items.Add(new WinForms.ToolStripMenuItem("New bucket…", null,
                (_, _) => NewBucketRequested?.Invoke()));
            menu.Items.Add(new WinForms.ToolStripMenuItem("Show all tiles", null,
                (_, _) => ShowAllRequested?.Invoke()));
            menu.Items.Add(new WinForms.ToolStripMenuItem("Settings…", null,
                (_, _) => SettingsRequested?.Invoke()));

            menu.Items.Add(new WinForms.ToolStripSeparator());

            _shellItem = new WinForms.ToolStripMenuItem("Add “New Bucket” to desktop right-click")
            {
                CheckOnClick = true,
                Checked = shellEnabled,
            };
            _shellItem.CheckedChanged += (_, _) =>
            {
                if (!_suppressShellEvents) ToggleShellRequested?.Invoke(_shellItem.Checked);
            };
            menu.Items.Add(_shellItem);

            menu.Items.Add(new WinForms.ToolStripMenuItem("Open buckets folder", null,
                (_, _) => OpenFolderRequested?.Invoke()));

            menu.Items.Add(new WinForms.ToolStripMenuItem("Realign desktop icons to grid", null,
                (_, _) => RealignIconsRequested?.Invoke())
            {
                ToolTipText = "Snaps every desktop icon to this app's own grid lines. Nothing changes which icon goes where — only nudges any that have drifted off-grid.",
            });

            menu.Items.Add(new WinForms.ToolStripSeparator());

            menu.Items.Add(new WinForms.ToolStripMenuItem("Check for updates…", null,
                (_, _) => CheckUpdatesRequested?.Invoke()));

            menu.Items.Add(new WinForms.ToolStripMenuItem("Quit Desktop Buckets", null,
                (_, _) => QuitRequested?.Invoke()));

            _icon = new WinForms.NotifyIcon
            {
                Text = "Desktop Buckets",
                Visible = true,
                Icon = LoadIcon(),
                ContextMenuStrip = menu,
            };
            // Left-click the tray icon -> Settings. (Right-click opens the menu.)
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left) SettingsRequested?.Invoke();
            };
        }

        private bool _suppressShellEvents;

        /// <summary>Reflects the real registration state without re-raising
        /// <see cref="ToggleShellRequested"/>. Without the guard, declining the UAC prompt
        /// (item checked, nothing registered) fed a "false" straight back into the
        /// handler and kicked off an uninstall.</summary>
        public void SetShellChecked(bool value)
        {
            if (_shellItem.Checked == value) return;
            _suppressShellEvents = true;
            try { _shellItem.Checked = value; }
            finally { _suppressShellEvents = false; }
        }

        public void ShowBalloon(string title, string text)
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.ShowBalloonTip(4000);
        }

        private static Icon LoadIcon()
        {
            try
            {
                var icoPath = Path.Combine(AppContext.BaseDirectory, "Resources", "app.ico");
                if (File.Exists(icoPath))
                    return new Icon(icoPath, new Size(32, 32));
            }
            catch (Exception) { }
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    var extracted = Icon.ExtractAssociatedIcon(exe);
                    if (extracted != null) return extracted;
                }
            }
            catch (Exception) { }
            return SystemIcons.Application;
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}

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
        public event Action<bool>? ToggleShellRequested;
        public event Action? OpenFolderRequested;
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

            menu.Items.Add(new WinForms.ToolStripSeparator());

            _shellItem = new WinForms.ToolStripMenuItem("Add “New Bucket” to desktop right-click")
            {
                CheckOnClick = true,
                Checked = shellEnabled,
            };
            _shellItem.CheckedChanged += (_, _) => ToggleShellRequested?.Invoke(_shellItem.Checked);
            menu.Items.Add(_shellItem);

            menu.Items.Add(new WinForms.ToolStripMenuItem("Open buckets folder", null,
                (_, _) => OpenFolderRequested?.Invoke()));

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
            _icon.DoubleClick += (_, _) => ShowAllRequested?.Invoke();
        }

        public void SetShellChecked(bool value)
        {
            if (_shellItem.Checked != value) _shellItem.Checked = value;
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

using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopBuckets.Interop;
using DesktopBuckets.Services;
using DesktopBuckets.ViewModels;

namespace DesktopBuckets.Views
{
    public partial class BucketTileWindow : Window
    {
        private readonly BucketTileViewModel _vm;
        private readonly IBucketHost _host;
        private readonly DispatcherTimer _zOrderTimer;
        private readonly DispatcherTimer _savePositionTimer;
        private readonly int _cascadeIndex;
        private bool _suppressZOrder;

        public BucketTileViewModel ViewModel => _vm;

        public BucketTileWindow(BucketTileViewModel vm, IBucketHost host, int cascadeIndex)
        {
            _vm = vm;
            _host = host;
            _cascadeIndex = cascadeIndex;
            InitializeComponent();
            DataContext = vm;

            BuildTileContextMenu();
            PlaceWindow(cascadeIndex);

            _zOrderTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(2),
            };
            _zOrderTimer.Tick += (_, _) =>
            {
                if (_suppressZOrder || !IsLoaded) return;
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero) DesktopWindowHelper.SendToBottom(hwnd);
            };

            _savePositionTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(400),
            };
            _savePositionTimer.Tick += (_, _) =>
            {
                _savePositionTimer.Stop();
                _vm.Bucket.SetPosition(Left, Top);
            };

            SourceInitialized += OnSourceInitialized;
            ContentRendered += OnContentRendered;
            LocationChanged += OnLocationChanged;
            MouseDoubleClick += OnMouseDoubleClick;
            Drop += OnDrop;
            DragEnter += (_, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                    ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
        }

        // ---- placement --------------------------------------------------

        private void PlaceWindow(int cascadeIndex)
        {
            var cfg = _vm.Bucket.Config;
            if (cfg.HasStoredPosition && IsOnScreen(cfg.X!.Value, cfg.Y!.Value))
            {
                Left = cfg.X!.Value;
                Top = cfg.Y!.Value;
                return;
            }

            double baseX = SystemParameters.WorkArea.Left + 40;
            double baseY = SystemParameters.WorkArea.Top + 40;
            int step = 28;
            Left = baseX + (cascadeIndex % 8) * step;
            Top = baseY + (cascadeIndex % 8) * step + (cascadeIndex / 8) * step * 3;
        }

        private static bool IsOnScreen(double x, double y)
        {
            double vx = SystemParameters.VirtualScreenLeft;
            double vy = SystemParameters.VirtualScreenTop;
            double vw = SystemParameters.VirtualScreenWidth;
            double vh = SystemParameters.VirtualScreenHeight;
            return x >= vx - 8 && y >= vy - 8 && x <= vx + vw - 40 && y <= vy + vh - 40;
        }

        // ---- window plumbing -----------------------------------------

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            DesktopWindowHelper.MakeDesktopWidget(this);
        }

        /// <summary>
        /// Borderless + transparent + non-activating windows frequently ship the
        /// native HWND at a stale size/position because the SizeToContent SetWindowPos
        /// races the first render. Once content has actually rendered we know the real
        /// size, so pin it and re-assert placement and Z-order here.
        /// </summary>
        private void OnContentRendered(object? sender, EventArgs e)
        {
            UpdateLayout();

            if (SizeToContent != SizeToContent.Manual)
            {
                double w = ActualWidth, h = ActualHeight;
                SizeToContent = SizeToContent.Manual;
                Width = w;
                Height = h;
            }

            PlaceWindow(_cascadeIndex);

            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) DesktopWindowHelper.SendToBottom(hwnd);

            _zOrderTimer.Start();
        }

        private void OnLocationChanged(object? sender, EventArgs e)
        {
            _savePositionTimer.Stop();
            _savePositionTimer.Start();
        }

        public void RefreshFromDisk() => _vm.Refresh();

        public void Teardown()
        {
            _zOrderTimer.Stop();
            _savePositionTimer.Stop();
            Close();
        }

        // ---- mouse: drag + open ------------------------------------

        private void Card_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ClickCount != 1) return;
            if (_vm.Bucket.Config.Locked) return;
            if (HitTestFile(e.OriginalSource) != null) return; // let the icon handle its own clicks

            try { DragMove(); }
            catch (InvalidOperationException) { /* button already released */ }
        }

        private void OnMouseDoubleClick(object? sender, MouseButtonEventArgs e)
        {
            var file = HitTestFile(e.OriginalSource);
            if (file != null)
                _vm.OpenFile(file);
            else
                _vm.OpenContainingFolder();
            e.Handled = true;
        }

        private static BucketFileViewModel? HitTestFile(object? originalSource)
        {
            var d = originalSource as DependencyObject;
            while (d != null)
            {
                if (d is FrameworkElement fe && fe.DataContext is BucketFileViewModel vm)
                    return vm;
                d = VisualTreeHelper.GetParent(d) ?? (d is FrameworkElement f ? f.Parent as DependencyObject : null);
            }
            return null;
        }

        // ---- per-file context menu -------------------------------

        private static BucketFileViewModel? FileFromMenu(object sender)
        {
            if (sender is not MenuItem mi) return null;
            if (mi.DataContext is BucketFileViewModel direct) return direct;

            DependencyObject? p = mi;
            while (p != null && p is not System.Windows.Controls.ContextMenu)
                p = LogicalTreeHelper.GetParent(p) ?? VisualTreeHelper.GetParent(p);
            if (p is System.Windows.Controls.ContextMenu cm && cm.PlacementTarget is FrameworkElement target)
                return target.DataContext as BucketFileViewModel;
            return null;
        }

        private void Ctx_OpenFile(object sender, RoutedEventArgs e)
        {
            if (FileFromMenu(sender) is { } f) _vm.OpenFile(f);
        }

        private void Ctx_TogglePin(object sender, RoutedEventArgs e)
        {
            if (FileFromMenu(sender) is { } f) _vm.TogglePin(f);
        }

        private void Ctx_Reveal(object sender, RoutedEventArgs e)
        {
            if (FileFromMenu(sender) is { } f) _vm.RevealInExplorer(f);
        }

        private void Ctx_CopyPath(object sender, RoutedEventArgs e)
        {
            if (FileFromMenu(sender) is { } f)
            {
                try { Clipboard.SetText(f.FullPath); } catch (Exception) { }
            }
        }

        // ---- tile (background) context menu ----------------------

        private void BuildTileContextMenu()
        {
            var menu = new ContextMenu();

            var open = new MenuItem { Header = "Open bucket folder" };
            open.Click += (_, _) => _vm.OpenContainingFolder();
            menu.Items.Add(open);

            var rename = new MenuItem { Header = "Rename bucket…" };
            rename.Click += (_, _) =>
            {
                var name = InputDialog.Ask("Rename bucket", "New name",
                    _vm.Bucket.Name, "Rename", this);
                if (!string.IsNullOrWhiteSpace(name))
                    _host.RenameBucket(_vm.Bucket, name!);
            };
            menu.Items.Add(rename);

            var slots = new MenuItem { Header = "Icon slots" };
            for (int n = 1; n <= 9; n++)
            {
                int count = n;
                var item = new MenuItem
                {
                    Header = n.ToString(),
                    IsCheckable = true,
                    IsChecked = _vm.Bucket.Config.SlotCount == n,
                };
                item.Click += (_, _) =>
                {
                    _vm.SetSlotCount(count);
                    foreach (var obj in slots.Items)
                        if (obj is MenuItem m)
                            m.IsChecked = m.Header?.ToString() == count.ToString();
                };
                slots.Items.Add(item);
            }
            menu.Items.Add(slots);

            var locked = new MenuItem
            {
                Header = "Lock position",
                IsCheckable = true,
                IsChecked = _vm.Bucket.Config.Locked,
            };
            locked.Click += (_, _) =>
            {
                _vm.Bucket.Config.Locked = locked.IsChecked;
                _vm.Bucket.SaveConfig();
            };
            menu.Items.Add(locked);

            menu.Items.Add(new Separator());

            var newBucket = new MenuItem { Header = "New bucket…" };
            newBucket.Click += (_, _) => _host.PromptCreateBucket();
            menu.Items.Add(newBucket);

            var shell = new MenuItem
            {
                Header = "Add “New Bucket” to desktop right-click",
                IsCheckable = true,
                IsChecked = _host.ShellIntegrationEnabled,
            };
            shell.Click += (_, _) => _host.ToggleShellIntegration(shell.IsChecked);
            menu.Items.Add(shell);

            menu.Items.Add(new Separator());

            var delete = new MenuItem { Header = "Delete bucket…" };
            delete.Click += (_, _) => _host.DeleteBucket(_vm.Bucket);
            menu.Items.Add(delete);

            menu.Opened += (_, _) => _suppressZOrder = true;
            menu.Closed += (_, _) => _suppressZOrder = false;

            Card.ContextMenu = menu;
        }

        // ---- drag & drop into the bucket -------------------------

        private void OnDrop(object? sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            var dest = _vm.Bucket.FolderPath;
            Directory.CreateDirectory(dest);

            foreach (var src in paths)
            {
                try
                {
                    if (Directory.Exists(src)) continue; // v1: files only
                    if (!File.Exists(src)) continue;

                    var targetPath = Path.Combine(dest, Path.GetFileName(src));
                    if (string.Equals(Path.GetDirectoryName(src), dest, StringComparison.OrdinalIgnoreCase))
                        continue; // already here

                    targetPath = UniqueName(targetPath);
                    File.Copy(src, targetPath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Drop copy failed: {ex.Message}");
                }
            }
            _vm.Refresh();
            e.Handled = true;
        }

        private static string UniqueName(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            int n = 2;
            string candidate;
            do { candidate = Path.Combine(dir, $"{name} ({n++}){ext}"); }
            while (File.Exists(candidate));
            return candidate;
        }
    }
}

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

        // manual drag + live desktop-icon displacement
        private readonly DesktopShell.DragDisplacement _displaced = new();
        private bool _dragging;
        private Interop.NativeMethods.POINT _dragMouseStartPx;
        private Point _dragWinStart;
        private double _dpiX = 1, _dpiY = 1;
        private (int col, int row) _lastDragCell = (int.MinValue, int.MinValue);
        private DesktopShell.IconGrid _dragGrid;
        private bool _dragGridValid;

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
                if (_suppressZOrder || !IsLoaded || !IsVisible) return;
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
            MouseMove += OnDragMouseMove;
            MouseLeftButtonUp += OnDragMouseUp;
            Drop += OnDrop;
            DragEnter += OnDragOver;
            DragOver += OnDragOver;

            // Content height changes a lot between an empty and a populated bucket;
            // re-fit the window whenever the contents change so nothing gets clipped.
            _vm.Refreshed += () => Dispatcher.BeginInvoke(
                new Action(RefitToContent), DispatcherPriority.Loaded);
        }

        private void RefitToContent()
        {
            if (!IsLoaded) return;
            try
            {
                SizeToContent = SizeToContent.WidthAndHeight;
                UpdateLayout();
                SizeToContent = SizeToContent.Manual;
                SizeToWholeCells(ActualWidth, ActualHeight);

                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero) DesktopWindowHelper.SendToBottom(hwnd);
                SnapToDesktopGrid(claimSpace: false);
            }
            catch (Exception ex) { Log.Error("RefitToContent failed", ex); }
        }

        // Whole-cell block the tile occupies (for snap + displacement), plus a gutter the
        // frosted rect overhangs into on every side so it reaches toward the neighbouring
        // icons. All four are in DEVICE PIXELS (the icon/listview frame).
        private double _gx, _gy, _blockW, _blockH;

        /// <summary>Sizes the window to a whole-cell block (2×2, 1×2, 3×3, …) plus a gutter
        /// overhang, so the visible tile hugs the surrounding icons. Content is DIP; the
        /// grid cell is device px, so convert through the window DPI.</summary>
        private void SizeToWholeCells(double contentWdip, double contentHdip)
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            double sxx = dpi.DpiScaleX, syy = dpi.DpiScaleY;
            var cell = DesktopShell.GetIconGrid().CellDip; // px

            // No gutter overhang: the window IS the whole-cell block, so a small bucket is
            // a true 2×2 that lines up cell-for-cell with the icons. Round to whole cells
            // with a tolerance so a slight content overflow doesn't jump to an extra cell.
            _gx = _gy = 0;
            if (cell.Width > 12 && cell.Height > 12)
            {
                double contentPxW = contentWdip * sxx, contentPxH = contentHdip * syy;
                int cols = Math.Max(1, (int)Math.Ceiling(contentPxW / cell.Width - 0.15));
                int rows = Math.Max(1, (int)Math.Ceiling(contentPxH / cell.Height - 0.15));
                _blockW = cols * cell.Width;
                _blockH = rows * cell.Height;
                Width = _blockW / sxx;
                Height = _blockH / syy;
            }
            else
            {
                _blockW = contentWdip * sxx; _blockH = contentHdip * syy;
                Width = contentWdip;
                Height = contentHdip;
            }
        }

        /// <summary>The tile window's rect in listview-client px (Rect.Empty on failure).</summary>
        private Rect TileClientRectPx()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return Rect.Empty;
            if (!Interop.NativeMethods.GetWindowRect(hwnd, out var wr)) return Rect.Empty;
            if (!DesktopShell.TryGetListViewRect(out var lv)) return Rect.Empty;
            return new Rect(wr.Left - lv.Left, wr.Top - lv.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);
        }

        /// <summary>The whole-cell footprint in client px, snapped to the grid (Rect.Empty
        /// on failure). Out-params give the raw block top-left for repositioning.</summary>
        private Rect SnappedBlockPx(DesktopShell.IconGrid grid)
        {
            var client = TileClientRectPx();
            if (client.IsEmpty) return Rect.Empty;
            var snapped = grid.Snap(new Point(client.X + _gx, client.Y + _gy));
            return new Rect(snapped.X, snapped.Y, _blockW, _blockH);
        }

        /// <summary>Move the window so its inner block lands on the snapped grid cell.</summary>
        private void SnapInnerBlock(DesktopShell.IconGrid grid)
        {
            var client = TileClientRectPx();
            if (client.IsEmpty) return;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            var blockTL = new Point(client.X + _gx, client.Y + _gy);
            var snapped = grid.Snap(blockTL);
            // moving the block by N client px == N screen px == N/dpi DIP on this monitor
            Left += (snapped.X - blockTL.X) / dpi.DpiScaleX;
            Top += (snapped.Y - blockTL.Y) / dpi.DpiScaleY;
        }

        private static void OnDragOver(object? sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = (e.KeyStates & DragDropKeyStates.ControlKey) != 0
                    ? DragDropEffects.Copy      // Ctrl held -> copy
                    : DragDropEffects.Move;     // default -> move into the bucket
            else
                e.Effects = DragDropEffects.None;
            e.Handled = true;
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
            AcrylicHelper.Apply(this);
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
                SizeToContent = SizeToContent.Manual;
                SizeToWholeCells(ActualWidth, ActualHeight);
            }

            PlaceWindow(_cascadeIndex);

            // Line up with the desktop icon grid, but don't rearrange the user's icons
            // on startup — that only happens on an explicit drag.
            SnapToDesktopGrid(claimSpace: false);

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

        /// <summary>Follow the desktop's "Show desktop icons" toggle.</summary>
        public void SetDesktopVisible(bool visible)
        {
            if (visible)
            {
                if (!IsVisible) Show();
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero) DesktopWindowHelper.SendToBottom(hwnd);
            }
            else if (IsVisible)
            {
                Hide();
            }
        }

        public void Teardown()
        {
            _zOrderTimer.Stop();
            _savePositionTimer.Stop();
            try { DesktopShell.RestoreDisplacement(_displaced, this); }
            catch (Exception ex) { Log.Error("RestoreDisplacement on teardown failed", ex); }
            Close();
        }

        // ---- mouse: drag + open ------------------------------------

        private void Card_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ClickCount != 1) return;
            if (_vm.Bucket.Config.Locked) return;
            if (HitTestFile(e.OriginalSource) != null) return; // let the icon handle its own clicks

            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            _dpiX = dpi.DpiScaleX;
            _dpiY = dpi.DpiScaleY;
            Interop.NativeMethods.GetCursorPos(out _dragMouseStartPx);
            _dragWinStart = new Point(Left, Top);
            _lastDragCell = (int.MinValue, int.MinValue);
            _dragging = true;
            _dragGridValid = false;
            CaptureMouse();
        }

        private void OnDragMouseMove(object? sender, MouseEventArgs e)
        {
            if (!_dragging) return;

            Interop.NativeMethods.GetCursorPos(out var cur);
            Left = _dragWinStart.X + (cur.X - _dragMouseStartPx.X) / _dpiX;
            Top = _dragWinStart.Y + (cur.Y - _dragMouseStartPx.Y) / _dpiY;

            if (!_vm.Bucket.Config.SnapToGrid || _vm.Bucket.Config.Locked) return;
            if (Math.Abs(Left - _dragWinStart.X) < 5 && Math.Abs(Top - _dragWinStart.Y) < 5) return;

            try
            {
                if (!_dragGridValid)
                {
                    // one stable grid snapshot for the whole drag
                    _dragGrid = DesktopShell.BeginDrag(_displaced);
                    _dragGridValid = _dragGrid.Valid;
                    if (!_dragGridValid) return;
                }

                var footprint = SnappedBlockPx(_dragGrid);
                if (footprint.IsEmpty) return;
                var cell = _dragGrid.CellOf(footprint.TopLeft);
                if (cell == _lastDragCell) return;
                _lastDragCell = cell;
                DesktopShell.UpdateDragDisplace(_displaced, footprint);
            }
            catch (Exception ex) { Log.Error("drag displace failed", ex); }
        }

        private void OnDragMouseUp(object? sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();

            bool moved = Math.Abs(Left - _dragWinStart.X) > 6 || Math.Abs(Top - _dragWinStart.Y) > 6;
            if (moved && _vm.Bucket.Config.SnapToGrid && !_vm.Bucket.Config.Locked && _dragGridValid)
            {
                try
                {
                    SnapInnerBlock(_dragGrid);
                    var fp = SnappedBlockPx(_dragGrid);
                    if (!fp.IsEmpty) DesktopShell.UpdateDragDisplace(_displaced, fp);
                }
                catch (Exception ex) { Log.Error("drop snap failed", ex); }
            }

            _vm.Bucket.SetPosition(Left, Top);
        }

        /// <param name="claimSpace">Also make desktop icons move aside (used by the
        /// "Snap to desktop grid" toggle; not on load).</param>
        private void SnapToDesktopGrid(bool claimSpace)
        {
            if (!_vm.Bucket.Config.SnapToGrid || _vm.Bucket.Config.Locked) return;
            try
            {
                var grid = DesktopShell.GetIconGrid();
                if (!grid.Valid) return;
                SnapInnerBlock(grid);
                if (claimSpace)
                {
                    DesktopShell.BeginDrag(_displaced);
                    var fp = SnappedBlockPx(grid);
                    if (!fp.IsEmpty) DesktopShell.UpdateDragDisplace(_displaced, fp);
                }
            }
            catch (Exception ex) { Log.Error("Grid snap failed", ex); }
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

            var snap = new MenuItem
            {
                Header = "Snap to desktop grid",
                IsCheckable = true,
                IsChecked = _vm.Bucket.Config.SnapToGrid,
            };
            snap.Click += (_, _) =>
            {
                _vm.Bucket.Config.SnapToGrid = snap.IsChecked;
                _vm.Bucket.SaveConfig();
                if (snap.IsChecked) SnapToDesktopGrid(claimSpace: true);
            };
            menu.Items.Add(snap);

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

            bool copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            int moved = 0, copied = 0, failed = 0;

            foreach (var src in paths)
            {
                try
                {
                    bool isDir = Directory.Exists(src);
                    if (!isDir && !File.Exists(src)) continue;

                    if (string.Equals(Path.GetDirectoryName(src), dest, StringComparison.OrdinalIgnoreCase))
                        continue; // already in this bucket

                    var target = UniqueName(Path.Combine(dest, Path.GetFileName(src)), isDir);

                    if (isDir)
                    {
                        if (copy) CopyDirectory(src, target);
                        else Directory.Move(src, target);
                    }
                    else
                    {
                        if (copy) File.Copy(src, target);
                        else File.Move(src, target);
                    }

                    if (copy) copied++; else moved++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.Error($"Drop {(copy ? "copy" : "move")} failed for '{src}'", ex);
                }
            }

            if (moved + copied + failed > 0)
                Log.Info($"Drop on '{_vm.Bucket.Name}': {moved} moved, {copied} copied, {failed} failed.");

            _vm.Refresh();
            e.Handled = true;
        }

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(src))
                CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        private static string UniqueName(string path, bool isDir = false)
        {
            bool Exists(string p) => isDir ? Directory.Exists(p) : File.Exists(p);
            if (!Exists(path) && !(isDir ? File.Exists(path) : Directory.Exists(path))) return path;

            var dir = Path.GetDirectoryName(path)!;
            var name = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
            var ext = isDir ? string.Empty : Path.GetExtension(path);
            int n = 2;
            string candidate;
            do { candidate = Path.Combine(dir, $"{name} ({n++}){ext}"); }
            while (File.Exists(candidate) || Directory.Exists(candidate));
            return candidate;
        }
    }
}

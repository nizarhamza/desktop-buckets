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

        // manual drag; desktop icons are pushed aside only once the tile RESTS in a
        // cell for DwellBeforeMakeSpace (or is dropped), never while it is moving
        private readonly DesktopShell.DragDisplacement _displaced = new();
        private static readonly TimeSpan DwellBeforeMakeSpace = TimeSpan.FromSeconds(1);
        private readonly DispatcherTimer _dwellTimer;
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

            _dwellTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = DwellBeforeMakeSpace,
            };
            _dwellTimer.Tick += (_, _) =>
            {
                _dwellTimer.Stop();
                if (_dragging) MakeSpaceUnderTile(snapped: false);
            };

            SourceInitialized += OnSourceInitialized;
            ContentRendered += OnContentRendered;
            LocationChanged += OnLocationChanged;
            MouseDoubleClick += OnMouseDoubleClick;
            MouseMove += OnDragMouseMove;
            MouseLeftButtonUp += OnDragMouseUp;
            LostMouseCapture += OnLostMouseCapture;
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
            var cell = DesktopShell.GetIconGrid().CellPx;

            // The window fills its whole-cell block exactly — edges land ON the desktop
            // icon-cell lines (matches the cell boxes shown when icons are selected). The
            // snap-drift fix (modal-phase origin) is what makes this line up.
            _gx = _gy = 0;
            if (cell.Width > 12 && cell.Height > 12)
            {
                double contentPxW = contentWdip * sxx, contentPxH = contentHdip * syy;

                // The icon grid's OWN row/column count (RecomputeGrid, e.g. 1 row for a
                // 2-file bucket) is the semantically correct cell-span — use it directly
                // rather than re-deriving one from rendered pixel size. Re-deriving via
                // ceiling(pixels / cellSize) rounds up to a WHOLE EXTRA cell the instant
                // content spills even slightly past one cell boundary, which the name
                // label reliably does for a 1-row bucket — doubling the tile's height for
                // no reason, mostly empty space. Math.Max still grows past the grid's own
                // count when content genuinely needs more (a long name, larger DPI/font),
                // so nothing clips; it just no longer force-inflates the common case.
                int vmCols = Math.Max(1, _vm.Columns);
                int vmRows = Math.Max(1, _vm.Rows);
                _blockW = Math.Max(vmCols * cell.Width, contentPxW);
                _blockH = Math.Max(vmRows * cell.Height, contentPxH);
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

        /// <summary>The tile window's rect in listview-client px (Rect.Empty on failure).
        /// Position comes from the managed <see cref="Left"/>/<see cref="Top"/>, size from
        /// <see cref="_blockW"/>/<see cref="_blockH"/> (set synchronously by
        /// <see cref="SizeToWholeCells"/>) — deliberately NOT a native <c>GetWindowRect</c>
        /// query, and deliberately not <c>ActualWidth</c>/<c>ActualHeight</c> either.
        /// <c>GetWindowRect</c> reflects the real HWND, which WPF updates asynchronously
        /// on the next layout pass after <c>Left</c>/<c>Top</c> change — under a fast
        /// drag it can read one frame stale, which is what left a released tile unsnapped
        /// (and icons still under it) after a rapid drag end. <c>ActualWidth</c> has the
        /// same lag risk right after <see cref="SizeToWholeCells"/> sets <c>Width</c>,
        /// before the next layout pass catches up — <c>_blockW</c> needs no layout pass,
        /// it's set the instant the size decision is made.</summary>
        private Rect TileClientRectPx()
        {
            if (!DesktopShell.TryGetListViewRect(out var lv)) return Rect.Empty;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            double screenLeftPx = Left * dpi.DpiScaleX;
            double screenTopPx = Top * dpi.DpiScaleY;
            return new Rect(screenLeftPx - lv.Left, screenTopPx - lv.Top, _blockW, _blockH);
        }

        /// <summary>The whole-cell footprint in client px, snapped to the grid (Rect.Empty
        /// on failure). Out-params give the raw block top-left for repositioning.</summary>
        private Rect SnappedBlockPx(DesktopShell.IconGrid grid)
        {
            var client = TileClientRectPx();
            if (client.IsEmpty) return Rect.Empty;
            // the block extends a margin OUTSIDE the visible window on every side
            var snapped = grid.Snap(new Point(client.X - _gx, client.Y - _gy));
            return new Rect(snapped.X, snapped.Y, _blockW, _blockH);
        }

        /// <summary>Move the window so its (centred) block lands on the snapped grid cell.</summary>
        private void SnapInnerBlock(DesktopShell.IconGrid grid)
        {
            var client = TileClientRectPx();
            if (client.IsEmpty) return;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            var blockTL = new Point(client.X - _gx, client.Y - _gy);
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
            _dwellTimer.Stop();
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

                // Moved to another cell: anything pushed aside for the previous spot
                // flows home, and the dwell clock restarts. Icons are only pushed once
                // the tile has rested here for a moment.
                DesktopShell.RestoreDisplacement(_displaced, this);
                _dwellTimer.Stop();
                _dwellTimer.Start();
            }
            catch (Exception ex) { Log.Error("drag tracking failed", ex); }
        }

        /// <summary>Push the desktop icons under the tile aside. While hovering the tile's
        /// ACTUAL rect is used (it may straddle cells), so nothing is pushed under it;
        /// after a drop the window sits on the snapped block, so pass that.</summary>
        private void MakeSpaceUnderTile(bool snapped)
        {
            if (!_dragGridValid) return;
            try
            {
                var rect = snapped ? SnappedBlockPx(_dragGrid) : TileClientRectPx();
                if (!rect.IsEmpty) DesktopShell.MakeSpace(_displaced, rect);
            }
            catch (Exception ex) { Log.Error("make space failed", ex); }
        }

        private void OnDragMouseUp(object? sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            FinishDrag();
        }

        /// <summary>Capture can be taken away mid-drag (UAC prompt, Win+D, display change,
        /// alt-tab). Without this the tile stayed glued to the pointer until the next
        /// click, with parked desktop icons left displaced.</summary>
        private void OnLostMouseCapture(object? sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            FinishDrag();
        }

        private void FinishDrag()
        {
            _dwellTimer.Stop();
            bool moved = Math.Abs(Left - _dragWinStart.X) > 6 || Math.Abs(Top - _dragWinStart.Y) > 6;
            if (moved && _vm.Bucket.Config.SnapToGrid && !_vm.Bucket.Config.Locked && _dragGridValid)
            {
                try
                {
                    SnapInnerBlock(_dragGrid);
                    // Dropped: the tile stays here, so make room right away.
                    MakeSpaceUnderTile(snapped: true);
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
                    _dragGrid = DesktopShell.BeginDrag(_displaced);
                    _dragGridValid = _dragGrid.Valid;
                    MakeSpaceUnderTile(snapped: true);
                }
            }
            catch (Exception ex) { Log.Error("Grid snap failed", ex); }
        }

        private void OnMouseDoubleClick(object? sender, MouseButtonEventArgs e)
        {
            var file = HitTestFile(e.OriginalSource);
            Report(file != null ? _vm.OpenFile(file) : _vm.OpenContainingFolder());
            e.Handled = true;
        }

        /// <summary>Surface a user-initiated action's failure message (null = success).</summary>
        private void Report(string? error)
        {
            if (error != null) _host.Notify(error);
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
            if (FileFromMenu(sender) is { } f) Report(_vm.OpenFile(f));
        }

        private void Ctx_TogglePin(object sender, RoutedEventArgs e)
        {
            if (FileFromMenu(sender) is { } f) _vm.TogglePin(f);
        }

        private void Ctx_Reveal(object sender, RoutedEventArgs e)
        {
            if (FileFromMenu(sender) is { } f) Report(_vm.RevealInExplorer(f));
        }

        private void Ctx_CopyPath(object sender, RoutedEventArgs e)
        {
            if (FileFromMenu(sender) is { } f)
            {
                try { Clipboard.SetText(f.FullPath); }
                catch (Exception ex)
                {
                    // Another app holding the clipboard open is the usual cause.
                    Log.Error("Copy path to clipboard failed", ex);
                    Report("Couldn't copy the path — the clipboard is in use by another app.");
                }
            }
        }

        // ---- tile (background) context menu ----------------------

        private void BuildTileContextMenu()
        {
            var menu = new ContextMenu();

            var open = new MenuItem { Header = "Open bucket folder" };
            open.Click += (_, _) => Report(_vm.OpenContainingFolder());
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

            var settings = new MenuItem { Header = "Settings…" };
            settings.Click += (_, _) => _host.OpenSettings();
            menu.Items.Add(settings);

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

        /// <summary>Files/folders dropped on the tile are moved (Ctrl = copied) into the
        /// bucket by the shell's own file engine on a worker thread: Explorer shows its
        /// progress dialog for anything slow, handles junctions/symlinks and cross-volume
        /// moves itself, and the operation is undoable with Ctrl+Z on the desktop.</summary>
        private async void OnDrop(object? sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Handled = true;

            var dest = _vm.Bucket.FolderPath;
            bool copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;

            var sources = new System.Collections.Generic.List<string>();
            foreach (var src in (string[])e.Data.GetData(DataFormats.FileDrop)!)
            {
                if (!Directory.Exists(src) && !File.Exists(src)) continue;
                if (string.Equals(Path.GetDirectoryName(src), dest, StringComparison.OrdinalIgnoreCase))
                    continue; // already in this bucket
                if (Directory.Exists(src) && dest.StartsWith(src.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    continue; // a folder can't be moved into itself
                sources.Add(src);
            }
            if (sources.Count == 0) return;

            try
            {
                Directory.CreateDirectory(dest);
                var r = await ShellFileOperations.TransferAsync(sources, dest, copy);
                Log.Info($"Drop on '{_vm.Bucket.Name}': {sources.Count} item(s) {(copy ? "copy" : "move")} -> " +
                         (r.Succeeded ? "ok" : r.Aborted ? "cancelled" : $"failed: {r.Error}"));
                if (!r.Succeeded && !r.Aborted)
                    Report($"Couldn't {(copy ? "copy" : "move")} into “{_vm.Bucket.Name}”: {r.Error}");
            }
            catch (Exception ex)
            {
                Log.Error("Drop failed", ex);
                Report($"Couldn't {(copy ? "copy" : "move")} into “{_vm.Bucket.Name}”: {ex.Message}");
            }

            if (IsLoaded) _vm.Refresh();
        }
    }
}

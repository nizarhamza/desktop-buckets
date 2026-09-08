using System;
using System.Collections.Generic;
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

        /// <summary>Cells THIS tile has currently parked a desktop icon in, so another
        /// tile's own MakeSpace can avoid landing an icon on top of one of these — see
        /// IBucketHost.OtherParkedCells.</summary>
        internal IEnumerable<(int col, int row)> ParkedCells => _displaced.Parked.Values.Select(p => p.Cell);

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

        // _blockW/_blockH: the whole-cell FOOTPRINT the tile reserves for snap +
        // displacement purposes (MakeSpace, SnappedBlockPx) — ALWAYS a whole multiple of
        // the grid cell. This must never be a partial cell: MakeSpace decides which
        // desktop cells are "under the tile" by testing geometric overlap against this
        // rect, so if it crept even a few px into a neighbouring row or column, that
        // whole row/column would be wrongly treated as blocked and its icons pushed —
        // a 2-file tile clearing a dozen icons instead of the 2 cells it actually needs.
        // _gx/_gy: how far the VISIBLE window is inset from that footprint on each side,
        // so a tile whose content doesn't fill its whole reserved block (a sparse 1-row
        // bucket reserving 2 cell-heights) is centred within it instead of stretched to
        // fill it. All four are in DEVICE PIXELS (the icon/listview frame).
        private double _gx, _gy, _blockW, _blockH;

        /// <summary>Sizes the window to fit its content — centred within a whole-cell
        /// footprint block reserved for snap + displacement. Content is DIP; the grid
        /// cell is device px, so convert through the window DPI.</summary>
        private void SizeToWholeCells(double contentWdip, double contentHdip)
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            double sxx = dpi.DpiScaleX, syy = dpi.DpiScaleY;
            var cell = DesktopShell.GetIconGrid().CellPx;

            if (cell.Width > 12 && cell.Height > 12)
            {
                double contentPxW = contentWdip * sxx, contentPxH = contentHdip * syy;

                // The footprint is whole cells, sized from the icon grid's OWN known
                // row/column count (RecomputeGrid, e.g. 1 row for a 2-file bucket) —
                // not re-derived from rendered pixel size, which would round up to a
                // whole EXTRA cell the instant content spills slightly past one cell
                // boundary (which the name label reliably does for a 1-row bucket).
                // Ceiling only grows it past that when content genuinely needs more
                // room (a long name, larger DPI/font) — always a WHOLE cell more, never
                // a partial one.
                int vmCols = Math.Max(1, _vm.Columns);
                int vmRows = Math.Max(1, _vm.Rows);
                int blockCols = Math.Max(vmCols, (int)Math.Ceiling(contentPxW / cell.Width - 0.12));
                int blockRows = Math.Max(vmRows, (int)Math.Ceiling(contentPxH / cell.Height - 0.12));
                _blockW = blockCols * cell.Width;
                _blockH = blockRows * cell.Height;

                // The visible window is only as big as its content needs — never larger
                // than the footprint (Min guards a stray content measurement from ever
                // exceeding the block it sits in). Left edge is NEVER inset: it always
                // sits exactly on the footprint's own left edge, i.e. on the grid line,
                // so tiles with different content widths (different file counts) still
                // line up on the same column — insetting it here (centring) made each
                // tile's visible left edge land at a DIFFERENT offset depending on its
                // own content width, even when snapped to the same column. Vertical DOES
                // still centre: that's what fixed a sparse 1-row bucket rendering as a
                // tall, mostly-empty tile, and height variance doesn't create the same
                // column-misalignment problem width variance does.
                _gx = 0;
                _gy = Math.Max(0, (_blockH - contentPxH) / 2);
                Width = Math.Min(contentPxW, _blockW) / sxx;
                Height = Math.Min(contentPxH, _blockH) / syy;
            }
            else
            {
                _blockW = contentWdip * sxx; _blockH = contentHdip * syy;
                _gx = _gy = 0;
                Width = contentWdip;
                Height = contentHdip;
            }
        }

        /// <summary>The tile WINDOW's own rect (its actual, possibly footprint-inset
        /// size) in listview-client px (Rect.Empty on failure). Position comes from the
        /// managed <see cref="Left"/>/<see cref="Top"/>, size from the managed
        /// <see cref="Width"/>/<see cref="Height"/> — deliberately NOT a native
        /// <c>GetWindowRect</c> query, and deliberately not <c>ActualWidth</c>/
        /// <c>ActualHeight</c> either. <c>GetWindowRect</c> reflects the real HWND,
        /// which WPF updates asynchronously on the next layout pass after Left/Top
        /// change — under a fast drag it can read one frame stale, which is what left a
        /// released tile unsnapped (and icons still under it) after a rapid drag end.
        /// <c>ActualWidth</c> has the same lag risk right after
        /// <see cref="SizeToWholeCells"/> sets <c>Width</c>, before the next layout pass
        /// catches up — the managed <c>Width</c>/<c>Height</c> need no layout pass,
        /// they're set the instant the size decision is made. Callers that need the
        /// whole-cell FOOTPRINT (not just the window) subtract <see cref="_gx"/>/
        /// <see cref="_gy"/> from this rect's position — see <see cref="SnappedBlockPx"/>.</summary>
        private Rect TileClientRectPx()
        {
            if (!DesktopShell.TryGetListViewRect(out var lv)) return Rect.Empty;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            double screenLeftPx = Left * dpi.DpiScaleX;
            double screenTopPx = Top * dpi.DpiScaleY;
            double widthPx = Width * dpi.DpiScaleX;
            double heightPx = Height * dpi.DpiScaleY;
            return new Rect(screenLeftPx - lv.Left, screenTopPx - lv.Top, widthPx, heightPx);
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

        /// <summary>Nudges this tile sideways, in whole grid-cell steps, if its rect
        /// overlaps another live tile — otherwise a placement based purely on cascade
        /// index or a stored position (that no longer fits, e.g. after another bucket
        /// was created nearby) can land two tiles on top of each other. Two problems
        /// this fixes together: a fresh tile visually stacking on an existing one, and
        /// (the worse half) that overlapping tile's own MakeSpace later treating the
        /// desktop cells hidden behind its neighbour as "free" and shoving real icons
        /// there, where they're invisible under the other tile. Only ever moves the
        /// tile AWAY from an actual overlap — it never relocates a tile that doesn't
        /// overlap anything, so tiles the user placed deliberately (even edge to edge)
        /// are left alone.</summary>
        private bool AvoidOtherTiles()
        {
            var others = _host.OtherTileRects(this).ToList();
            if (others.Count == 0) return false;

            var mine = new Rect(Left, Top, ActualWidth, ActualHeight);
            var cell = DesktopShell.GetIconGrid().CellPx;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            var step = new Size(
                cell.Width > 4 ? cell.Width / dpi.DpiScaleX : 32,
                cell.Height > 4 ? cell.Height / dpi.DpiScaleY : 32);

            if (DesktopShell.FindNonOverlappingPosition(mine, others, step, IsOnScreen) is not { } p)
                return false;

            Left = p.X;
            Top = p.Y;
            return true;
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

            // Other tiles may already be showing (loaded earlier this same startup, or
            // this bucket's own stored position no longer clears one created since).
            // Step away from any overlap before it can hide icons behind a neighbour.
            if (AvoidOtherTiles())
            {
                SnapToDesktopGrid(claimSpace: false);
                _vm.Bucket.SetPosition(Left, Top); // persist so the fix sticks, not just this run
            }

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

            // Ground truth for whether the button is actually still down, independent of
            // capture. This window is WS_EX_NOACTIVATE + non-focusable, and a missed
            // MouseLeftButtonUp/LostMouseCapture (observed live: capture taken but the
            // matching up-event never routes back, most likely raced against the
            // periodic SendToBottom Z-order change) otherwise strands _dragging=true
            // forever. Once stranded, EVERY later mouse move anywhere on the desktop —
            // not just over this tile — gets misread as this tile still being dragged:
            // it teleports to wherever the real cursor currently is and repeatedly
            // triggers MakeSpace on whatever cell it passes over, invisibly, in the
            // background. That's the "icons move arbitrarily" / "unsmooth movement"
            // symptom reported earlier, and the log's continuous "make space" churn
            // tracking ordinary mouse activity instead of an actual tile drag. Self-heal
            // on the very next move event we do see, rather than trusting our own flag.
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _dragging = false;
                ReleaseMouseCapture();
                FinishDrag();
                return;
            }

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
                if (!rect.IsEmpty) DesktopShell.MakeSpace(_displaced, rect, _host.OtherParkedCells(this));
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

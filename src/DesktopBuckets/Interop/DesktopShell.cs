using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace DesktopBuckets.Interop
{
    /// <summary>
    /// Ties tiles to the real desktop: the icon grid (origin + cell, from the actual
    /// icons), pushing icons aside to make room for a tile, and whether "Show desktop
    /// icons" is on. Every method is best-effort and never throws.
    /// </summary>
    internal static class DesktopShell
    {
        /// <summary>The desktop icon grid. <b>Every value is in desktop-listview client
        /// pixels</b> — the frame raw <c>LVM_GETITEMPOSITION</c> reports in — never WPF
        /// DIPs. Convert through the window's DPI scale at the edge, not here.
        ///
        /// Windows lays desktop icons out per monitor: each monitor's lattice is anchored
        /// at the top-left of that monitor's <i>work area</i> and steps by the listview's
        /// item spacing. An icon's reported position is its image inside the cell (about
        /// (cell − icon)/2 in from the left, 2 px down), NOT the cell corner — treating
        /// it as the corner is what used to shift every snap by that padding.
        ///
        /// Cells are addressed as (col, row) with the monitor index folded into
        /// <c>col</c> (see <see cref="Encode"/>), so the rest of the code can keep using
        /// plain tuples while never confusing two monitors' lattices.</summary>
        public readonly struct IconGrid
        {
            public readonly Size CellPx;
            public readonly List<Rect> Icons;
            /// <summary>Work areas in client px, primary first. Anchor of monitor m = Monitors[m].TopLeft.</summary>
            public readonly List<Rect> Monitors;

            private const int Stride = 1000;   // per-monitor column band
            private const int Bias = Stride / 2; // so a column a little left of the anchor still decodes

            public IconGrid(List<Rect> monitors, Size cell, List<Rect> icons)
            {
                Monitors = monitors; CellPx = cell; Icons = icons;
            }

            public bool Valid => CellPx.Width > 4 && CellPx.Height > 4 && Monitors is { Count: > 0 };

            /// <summary>Anchor of the primary monitor (logging / legacy callers).</summary>
            public Point OriginPx => Monitors is { Count: > 0 } ? Monitors[0].TopLeft : default;

            /// <summary>Index of the monitor containing <paramref name="p"/>, else the nearest.</summary>
            public int MonitorOf(Point p)
            {
                for (int i = 0; i < Monitors.Count; i++)
                    if (Monitors[i].Contains(p)) return i;
                int best = 0; double bestD = double.MaxValue;
                for (int i = 0; i < Monitors.Count; i++)
                {
                    var r = Monitors[i];
                    double dx = Math.Max(0, Math.Max(r.Left - p.X, p.X - r.Right));
                    double dy = Math.Max(0, Math.Max(r.Top - p.Y, p.Y - r.Bottom));
                    double d = dx * dx + dy * dy;
                    if (d < bestD) { bestD = d; best = i; }
                }
                return best;
            }

            public static int Encode(int monitor, int localCol) => monitor * Stride + Bias + localCol;
            public static int MonitorOfCol(int col) => (int)Math.Floor(col / (double)Stride);
            public static int LocalCol(int col) => col - MonitorOfCol(col) * Stride - Bias;

            /// <summary>Work area (client px) of the monitor a cell belongs to.</summary>
            public Rect MonitorRect(int col) => Monitors[Math.Clamp(MonitorOfCol(col), 0, Monitors.Count - 1)];

            public (int col, int row) CellOf(Point p)
            {
                int m = MonitorOf(p);
                var a = Monitors[m].TopLeft;
                return (Encode(m, (int)Math.Round((p.X - a.X) / CellPx.Width)),
                        (int)Math.Round((p.Y - a.Y) / CellPx.Height));
            }

            public Point CellTopLeft(int col, int row)
            {
                var a = MonitorRect(col).TopLeft;
                return new(a.X + LocalCol(col) * CellPx.Width, a.Y + row * CellPx.Height);
            }

            public Rect CellRect(int col, int row)
            {
                var tl = CellTopLeft(col, row);
                return new Rect(tl.X, tl.Y, CellPx.Width, CellPx.Height);
            }

            internal bool SameAs(in IconGrid other) =>
                Monitors.Count == other.Monitors.Count && OriginPx == other.OriginPx
                && CellPx == other.CellPx && Icons.Count == other.Icons.Count;

            /// <summary>Snap a point to the nearest cell's top-left on its monitor's lattice.</summary>
            public Point Snap(Point p)
            {
                var c = CellOf(p);
                return CellTopLeft(c.col, c.row);
            }

            /// <summary>Offset of a point inside its cell (0..cell): 0 means exactly on a line.</summary>
            public Point PhaseOf(Point p)
            {
                var a = Monitors[MonitorOf(p)].TopLeft;
                return new(((p.X - a.X) % CellPx.Width + CellPx.Width) % CellPx.Width,
                           ((p.Y - a.Y) % CellPx.Height + CellPx.Height) % CellPx.Height);
            }
        }

        // EVERYTHING here works in desktop-listview CLIENT PIXELS — the one frame that
        // icons (raw LVM_GETITEMPOSITION) and the tile (GetWindowRect minus the listview
        // origin) both live in. No WPF DIP, no per-monitor DPI mixing.

        /// <summary>The icon grid cell in device pixels: the desktop listview's own item
        /// spacing (<c>LVM_GETITEMSPACING</c>), which is exactly what "Align icons to
        /// grid" snaps to. The SPI_ICON*SPACING metric is only a fallback — on this
        /// machine it reads 93×75 while the listview really uses 96×100.</summary>
        public static Size GridCellPx()
        {
            try
            {
                var lv = ResolveListView();
                if (lv != IntPtr.Zero &&
                    NativeMethods.TrySendMessage(lv, NativeMethods.LVM_GETITEMSPACING, IntPtr.Zero, IntPtr.Zero, out var r))
                {
                    long v = r.ToInt64();
                    int cx = (int)(v & 0xFFFF), cy = (int)((v >> 16) & 0xFFFF);
                    if (cx > 16 && cy > 16) return new Size(cx, cy);
                }
            }
            catch { }

            int h = 0, vv = 0;
            try
            {
                if (!NativeMethods.SystemParametersInfo(NativeMethods.SPI_ICONHORIZONTALSPACING, 0, ref h, 0)) h = 0;
                if (!NativeMethods.SystemParametersInfo(NativeMethods.SPI_ICONVERTICALSPACING, 0, ref vv, 0)) vv = 0;
            }
            catch { }
            if (h <= 0) h = 90;
            if (vv <= 0) vv = 110;
            return new Size(h, vv);
        }

        /// <summary>Screen rect of the desktop icon listview (device px). Callers subtract
        /// its origin to convert a window's screen rect into listview-client px.</summary>
        public static bool TryGetListViewRect(out NativeMethods.RECT rect)
        {
            rect = default;
            var lv = ResolveListView();
            return lv != IntPtr.Zero && NativeMethods.GetWindowRect(lv, out rect);
        }

        // Reading the grid means a handle on explorer.exe plus two cross-process calls
        // per desktop icon. It is asked for on every tile refresh, pin toggle and drop,
        // so cache it briefly and drop the cache when the shell tells us something
        // changed (display / DPI / SPI settings via InvalidateGridCache).
        private static readonly TimeSpan GridCacheTtl = TimeSpan.FromSeconds(1);
        private static IconGrid _cachedGrid;
        private static DateTime _cachedGridAt = DateTime.MinValue;
        private static IconGrid _lastLoggedGrid;
        private static bool _loggedOnce;

        /// <summary>Forget the cached grid (display / DPI / icon-spacing change).</summary>
        public static void InvalidateGridCache() => _cachedGridAt = DateTime.MinValue;

        /// <summary>Real desktop grid in client px: origin = first column/row line,
        /// cell = measured pitch (spacing metric only as fallback). Cached for
        /// <see cref="GridCacheTtl"/>; pass <paramref name="fresh"/> to bypass.</summary>
        public static IconGrid GetIconGrid(bool fresh = false)
        {
            if (!fresh && DateTime.UtcNow - _cachedGridAt < GridCacheTtl)
                return _cachedGrid;
            var grid = ReadIconGrid();
            _cachedGrid = grid;
            _cachedGridAt = DateTime.UtcNow;
            return grid;
        }

        private static IconGrid ReadIconGrid()
        {
            var cell = GridCellPx();
            var icons = DesktopIconCells(cell);
            var monitors = MonitorWorkAreasClientPx();

            var grid = new IconGrid(monitors, cell, icons);
            // Log only when the answer changes — this used to be a locked file append
            // on the UI thread for every refresh.
            if (!_loggedOnce || !grid.SameAs(_lastLoggedGrid))
            {
                var mons = string.Join(" ", monitors.ConvertAll(m => $"({m.X},{m.Y})+{m.Width}x{m.Height}"));
                Services.Log.Info($"IconGrid(px): cell={cell.Width:F0}x{cell.Height:F0} monitors={mons} icons={icons.Count}");
                _lastLoggedGrid = grid;
                _loggedOnce = true;
            }
            return grid;
        }

        /// <summary>Every monitor's work area in listview-client px, primary first. Falls
        /// back to the listview's own client rect as a single "monitor".</summary>
        private static List<Rect> MonitorWorkAreasClientPx()
        {
            var result = new List<(bool primary, Rect work)>();
            try
            {
                if (TryGetListViewRect(out var lr))
                {
                    NativeMethods.MonitorEnumProc cb = (IntPtr hMon, IntPtr _, ref NativeMethods.RECT __, IntPtr ___) =>
                    {
                        var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                        if (NativeMethods.GetMonitorInfo(hMon, ref mi))
                        {
                            var w = mi.rcWork;
                            result.Add(((mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                                new Rect(w.Left - lr.Left, w.Top - lr.Top, w.Right - w.Left, w.Bottom - w.Top)));
                        }
                        return true;
                    };
                    NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
                    GC.KeepAlive(cb);
                }
            }
            catch (Exception ex) { Services.Log.Error("EnumDisplayMonitors failed", ex); }

            result.Sort((a, b) => b.primary.CompareTo(a.primary)); // primary first, stable otherwise
            var list = result.ConvertAll(r => r.work);
            if (list.Count == 0)
            {
                var lv = ResolveListView();
                if (lv != IntPtr.Zero && NativeMethods.GetClientRect(lv, out var cr))
                    list.Add(new Rect(0, 0, cr.Right - cr.Left, cr.Bottom - cr.Top));
            }
            return list;
        }

        /// <summary>Diagnostic for <c>--dump-grid</c>: every tile window (title "Bucket")
        /// with its rect in screen px and listview-client px, and where its edges fall
        /// relative to the icon lattice.</summary>
        public static string DescribeTileWindows()
        {
            var sb = new System.Text.StringBuilder();
            if (!TryGetListViewRect(out var lr)) return "no listview rect\n";
            var grid = GetIconGrid();
            var buf = new System.Text.StringBuilder(64);
            NativeMethods.EnumWindowsProc cb = (h, _) =>
            {
                buf.Clear();
                NativeMethods.GetWindowText(h, buf, buf.Capacity);
                if (buf.ToString() != "Bucket" || !NativeMethods.IsWindowVisible(h)) return true;
                if (!NativeMethods.GetWindowRect(h, out var wr)) return true;
                var client = new Rect(wr.Left - lr.Left, wr.Top - lr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);
                var ph = grid.PhaseOf(client.TopLeft);
                var phBr = grid.PhaseOf(client.BottomRight);
                sb.AppendLine($"tile hwnd=0x{h.ToInt64():X} screen=({wr.Left},{wr.Top})-({wr.Right},{wr.Bottom}) " +
                              $"client=({client.X},{client.Y}) size={client.Width}x{client.Height} " +
                              $"topleft-in-cell=({ph.X:F0},{ph.Y:F0}) bottomright-in-cell=({phBr.X:F0},{phBr.Y:F0}) " +
                              $"cell={grid.CellOf(client.TopLeft)}");
                return true;
            };
            NativeMethods.EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return sb.Length == 0 ? "no tile windows found\n" : sb.ToString();
        }

        /// <summary>Diagnostic for <c>--dump-grid</c>: everything the snapping code sees,
        /// as text. Includes the phase histogram (how many icons sit at each offset
        /// within a cell) — on an aligned desktop one bucket holds nearly all of them.</summary>
        public static string DescribeGrid()
        {
            var sb = new System.Text.StringBuilder();
            var lv = ResolveListView();
            sb.AppendLine($"listview hwnd=0x{lv.ToInt64():X}");
            if (TryGetListViewRect(out var lr))
                sb.AppendLine($"listview screen rect=({lr.Left},{lr.Top})-({lr.Right},{lr.Bottom})");
            var cell = GridCellPx();
            sb.AppendLine($"cell (LVM_GETITEMSPACING or fallback)={cell.Width}x{cell.Height}");
            var grid = GetIconGrid(fresh: true);
            {
                var xs = new List<double>(); var ys = new List<double>();
                foreach (var r in grid.Icons) { var ph = grid.PhaseOf(r.TopLeft); xs.Add(ph.X); ys.Add(ph.Y); }
                if (xs.Count > 0) { xs.Sort(); ys.Sort(); sb.AppendLine($"icon padding (median phase)=({xs[xs.Count / 2]:F0},{ys[ys.Count / 2]:F0})"); }
            }
            for (int i = 0; i < grid.Monitors.Count; i++)
                sb.AppendLine($"monitor {i} work area (client px)=({grid.Monitors[i].X},{grid.Monitors[i].Y}) {grid.Monitors[i].Width}x{grid.Monitors[i].Height}");
            sb.AppendLine($"icons={grid.Icons.Count} valid={grid.Valid}");
            if (grid.Icons.Count > 0)
            {
                // Phase relative to each icon's monitor anchor = the icon's padding inside its cell.
                sb.AppendLine("icon x-phase histogram: " + Histogram(grid.Icons.ConvertAll(r => grid.PhaseOf(r.TopLeft).X), cell.Width));
                sb.AppendLine("icon y-phase histogram: " + Histogram(grid.Icons.ConvertAll(r => grid.PhaseOf(r.TopLeft).Y), cell.Height));
                // One entry per icon in listview item order: monitor:col,row
                var cells = grid.Icons.ConvertAll(r =>
                {
                    var c = grid.CellOf(r.TopLeft);
                    return $"{IconGrid.MonitorOfCol(c.col)}:{IconGrid.LocalCol(c.col)},{c.row}";
                });
                sb.AppendLine("icon cells: " + string.Join(" ", cells));
            }
            return sb.ToString();

            static string Histogram(List<double> values, double cell)
            {
                // (helper for DescribeGrid)
                var b = new Dictionary<int, int>();
                foreach (var v in values) { int ph = (int)Math.Round(((v % cell) + cell) % cell); b[ph] = b.TryGetValue(ph, out var n) ? n + 1 : 1; }
                var items = new List<KeyValuePair<int, int>>(b);
                items.Sort((a, c) => c.Value.CompareTo(a.Value));
                var parts = new List<string>();
                foreach (var kv in items) parts.Add($"{kv.Key}px×{kv.Value}");
                return string.Join(" ", parts);
            }
        }

        private static Rect Inset(Rect r, double d) =>
            new(r.X + d, r.Y + d, Math.Max(1, r.Width - 2 * d), Math.Max(1, r.Height - 2 * d));

        // ---- making space for a resting tile ------------------------------

        /// <summary>Tracks which desktop icons a tile has pushed aside. A snapshot of the
        /// grid and every icon's resting position is captured when a drag starts, so
        /// recomputing while icons animate can't drift. Icons are moved only once the
        /// tile has come to rest (BucketTileWindow's dwell timer) or is dropped, and all
        /// slide home as soon as it moves on or goes away.</summary>
        public sealed class DragDisplacement
        {
            internal IconGrid Grid;
            internal bool Captured;
            // icon index -> its untouched resting position (client px)
            internal readonly Dictionary<int, Point> HomePx = new();
            // icons currently pushed aside: index -> (home px, cell they were pushed to)
            internal readonly Dictionary<int, (Point Home, (int col, int row) Cell)> Parked = new();
            public bool Any => Parked.Count > 0;

            public void Reset() { Captured = false; HomePx.Clear(); Parked.Clear(); }
        }

        /// <summary>Prepare for a (possibly repeat) drag: capture the grid and every icon's
        /// TRUE resting position — except icons still parked from a previous drag, whose
        /// recorded home is kept so they can still return there.</summary>
        public static IconGrid BeginDrag(DragDisplacement state, Visual? forWindow = null)
        {
            EnsureSnapToGridDisabled(); // so Explorer's own grid can't fight this drag's placements
            WithListView((lv, proc, remote) =>
            {
                int count = ItemCount(lv);
                if (count <= 0 || count > 5000) return;
                var grid0 = GetIconGrid(fresh: true); // a drag snapshot must not be a stale cache hit
                if (!grid0.Valid) return;
                var pos0 = ReadPositions(lv, proc, remote, count);
                state.Grid = grid0;
                for (int i = 0; i < count; i++)
                {
                    if (state.Parked.ContainsKey(i)) continue;      // keep its real home
                    if (!double.IsNaN(pos0[i].X)) state.HomePx[i] = pos0[i];
                }
                state.Captured = true;
            });
            return state.Grid;
        }

        /// <summary>Clears the desktop listview's own "Align icons to grid" extended
        /// style (LVS_EX_SNAPTOGRID) so Explorer never re-snaps an icon we just placed to
        /// ITS OWN idea of the grid, fighting this app's placement. Idempotent and cheap;
        /// safe to call before every push. Does NOT touch "Auto arrange icons"
        /// (LVS_AUTOARRANGE) — that's a much bigger behavior change the user owns, and
        /// MakeSpace already refuses to run at all while it's on.</summary>
        public static void EnsureSnapToGridDisabled()
        {
            try
            {
                var lv = ResolveListView();
                if (lv == IntPtr.Zero) return;
                if (!NativeMethods.TrySendMessage(lv, NativeMethods.LVM_GETEXTENDEDLISTVIEWSTYLE, IntPtr.Zero, IntPtr.Zero, out var cur))
                    return;
                long ex = cur.ToInt64();
                if ((ex & NativeMethods.LVS_EX_SNAPTOGRID) == 0) return; // already off
                long next = ex & ~(long)NativeMethods.LVS_EX_SNAPTOGRID;
                NativeMethods.TrySendMessage(lv, NativeMethods.LVM_SETEXTENDEDLISTVIEWSTYLE,
                    (IntPtr)NativeMethods.LVS_EX_SNAPTOGRID, IntPtr.Zero, out _);
                Services.Log.Info("Disabled the desktop's own \"Align icons to grid\" so it can't fight tile placement.");
            }
            catch (Exception ex) { Services.Log.Error("EnsureSnapToGridDisabled failed", ex); }
        }

        /// <summary>User-invoked "Realign desktop icons to grid": snaps every desktop
        /// icon to the nearest cell of THIS app's own measured lattice (animated), so any
        /// icon that has drifted off-grid — from Windows, from another app, or from a
        /// past version of this one — lines back up. Icons already exactly on a cell are
        /// left untouched. Never rearranges which icon is where, only nudges each to its
        /// own nearest line.</summary>
        public static int RealignAllIconsToGrid()
        {
            int moved = 0;
            try
            {
                WithListView((lv, proc, remote) =>
                {
                    if ((NativeMethods.GetWindowLong(lv, NativeMethods.GWL_STYLE) & NativeMethods.LVS_AUTOARRANGE) != 0)
                        return; // Explorer owns placement entirely in this mode; nothing for us to do
                    var grid = GetIconGrid(fresh: true);
                    if (!grid.Valid) return;

                    int count = ItemCount(lv);
                    if (count <= 0 || count > 5000) return;
                    var pos = ReadPositions(lv, proc, remote, count);

                    // The icon IMAGE sits inset within its cell (24,2 px typically) — the
                    // same measurement MakeSpace uses. Snapping to the bare cell corner
                    // (grid.Snap alone) puts every icon at the wrong sub-cell position.
                    var pad = MedianPhase(grid, pos.Where(p => !double.IsNaN(p.X)));

                    // Two icons that started close together can round to the SAME
                    // nearest cell; moving both there without checking leaves them
                    // exactly on top of each other (overlapping icon + garbled,
                    // interleaved label text) — resolved by ResolveCellCollisions.
                    var home = new Dictionary<int, (int col, int row)>();
                    for (int i = 0; i < count; i++)
                        if (!double.IsNaN(pos[i].X)) home[i] = grid.CellOf(pos[i]);

                    var finalCell = ResolveCellCollisions(home, grid);
                    int reHomedCount = home.Count - finalCell.Count(kv => home[kv.Key] == kv.Value);

                    int stagger = 0;
                    foreach (var kv in finalCell)
                    {
                        int i = kv.Key;
                        var tl = grid.CellTopLeft(kv.Value.col, kv.Value.row);
                        var target = new Point(tl.X + pad.X, tl.Y + pad.Y);
                        if (Math.Abs(target.X - pos[i].X) < 1 && Math.Abs(target.Y - pos[i].Y) < 1) continue;
                        IconAnimator.Move(lv, i, pos[i], target, delayMs: Math.Min(stagger++ * 12, 400));
                        moved++;
                    }
                    Services.Log.Info($"Realign to grid: moved {moved} of {count} icon(s), {reHomedCount} re-homed to resolve a collision.");
                });
            }
            catch (Exception ex) { Services.Log.Error("RealignAllIconsToGrid failed", ex); }
            return moved;
        }

        /// <summary>Push the icons under <paramref name="tileRectPx"/> out of the way the
        /// way the Windows desktop does when something is dropped into a column: every
        /// column the tile covers gets its occupied rows (from the tile's bottom edge
        /// down) shifted down to just past the tile, in original top-to-bottom order.
        /// Whatever doesn't fit below the tile in that column carries over — landing
        /// first — at the top of the next column to the right, which then shifts its own
        /// icons down in turn; a tile spanning several columns cascades through all of
        /// them left to right. Icons above the tile, and every column to its left, never
        /// move. "Under" and "blocked" are judged against the tile's actual rectangle and
        /// each icon's visible area (image + label inside the cell), so nothing is ever
        /// placed into a cell the tile covers, even while it hovers off-grid. Icons
        /// already parked stay put unless the tile now covers them.</summary>
        public static void MakeSpace(DragDisplacement state, Rect tileRectPx)
        {
            if (!state.Captured) BeginDrag(state);
            if (!state.Captured) return;

            WithListView((lv, proc, remote) =>
            {
                if ((NativeMethods.GetWindowLong(lv, NativeMethods.GWL_STYLE) & NativeMethods.LVS_AUTOARRANGE) != 0)
                    return;

                var grid = state.Grid;
                if (state.HomePx.Count == 0) return;

                // How far the icon image sits inside its cell (24,2 px on a default
                // desktop): the median offset of the icons from their cell corners.
                var pad = IconPadding(grid, state);
                var iconSize = new Size(Math.Max(8, grid.CellPx.Width - 2 * pad.X),
                                        Math.Max(8, grid.CellPx.Height - 2 * pad.Y));
                Rect VisualOf((int col, int row) cell)
                {
                    var tl = grid.CellTopLeft(cell.col, cell.row);
                    return new Rect(tl.X + pad.X, tl.Y + pad.Y, iconSize.Width, iconSize.Height);
                }
                // Grown by 1px so a cell exactly edge-adjacent to the tile still counts
                // as blocked rather than slipping through on float rounding.
                bool Blocked((int col, int row) cell) => Inset(VisualOf(cell), -1).IntersectsWith(tileRectPx);

                var tlCell = grid.CellOf(new Point(tileRectPx.Left + 1, tileRectPx.Top + 1));
                var bounds = grid.MonitorRect(tlCell.col);
                int monitor = IconGrid.MonitorOfCol(tlCell.col);
                int maxCol = Math.Max(0, (int)Math.Floor(bounds.Width / grid.CellPx.Width) - 1);
                int maxRow = Math.Max(0, (int)Math.Floor(bounds.Height / grid.CellPx.Height) - 1);

                // Every cell this tile covers on its monitor — scanned in full so a wide
                // or oddly-placed tile is never under-detected.
                int tileLeft = int.MaxValue, tileRight = int.MinValue, tileTop = int.MaxValue, tileBottom = int.MinValue;
                for (int c = 0; c <= maxCol; c++)
                for (int r = 0; r <= maxRow; r++)
                    if (Blocked((IconGrid.Encode(monitor, c), r)))
                    {
                        tileLeft = Math.Min(tileLeft, c); tileRight = Math.Max(tileRight, c);
                        tileTop = Math.Min(tileTop, r); tileBottom = Math.Max(tileBottom, r);
                    }
                if (tileLeft == int.MaxValue) return; // the tile isn't over this monitor's grid at all

                // Where every icon is right now (parked icons at their parking cell, the
                // rest at home) — this map is read-only for the rest of the method, so
                // "before" and "after" positions never get confused mid-computation.
                var before = new Dictionary<int, (int col, int row)>();
                var localCells = new Dictionary<int, (int col, int row)>(); // idx -> LOCAL (monitor-relative) cell, this monitor only
                foreach (var kv in state.HomePx)
                {
                    int idx = kv.Key;
                    var cell = state.Parked.TryGetValue(idx, out var p) ? p.Cell : grid.CellOf(kv.Value);
                    before[idx] = cell;
                    if (IconGrid.MonitorOfCol(cell.col) != monitor) continue;   // a different monitor: never touched
                    localCells[idx] = (IconGrid.LocalCol(cell.col), cell.row);
                }

                var placements = ComputeColumnPush(localCells, tileLeft, tileRight, tileTop, tileBottom, maxCol, maxRow);
                var moves = new List<(int idx, (int col, int row) to, int stagger)>();
                foreach (var pl in placements)
                {
                    var to = (IconGrid.Encode(monitor, pl.to.col), pl.to.row);
                    if (before[pl.idx] != to) moves.Add((pl.idx, to, pl.stage));
                }

                // Whatever ComputeColumnPush couldn't fit anywhere on the grid (monitor
                // packed solid): place at the nearest free cell that isn't under the tile.
                // Should be exceedingly rare.
                if (placements.Overflow.Count > 0)
                {
                    var occSet = OccupiedAfterMoves(before, moves);
                    int stage = placements.NextStage;
                    foreach (var idx in placements.Overflow)
                    {
                        if (FindFreeCell(grid, before[idx], new HashSet<(int, int)>(), occSet, bounds, tileRectPx) is not { } near)
                        {
                            Services.Log.Error($"make space: no room left on the monitor for icon {idx}; leaving it under the tile.");
                            continue;
                        }
                        moves.Add((idx, near, stage));
                        occSet.Add(near);
                    }
                }

                if (moves.Count == 0) return;

                foreach (var (idx, to, s) in moves)
                {
                    if (Blocked(to)) // should be unreachable; guards against a future regression silently landing icons under the tile
                        Services.Log.Error($"make space: computed landing cell {to} for icon {idx} is under the tile — skipping the move.");
                    else
                    {
                        var homePx = state.Parked.TryGetValue(idx, out var pk) ? pk.Home : state.HomePx[idx];
                        var fromPx = state.Parked.ContainsKey(idx)
                            ? grid.CellTopLeft(before[idx].col, before[idx].row)
                            : state.HomePx[idx];
                        var toPx = grid.CellTopLeft(to.col, to.row);
                        IconAnimator.Move(lv, idx, fromPx, new Point(toPx.X + pad.X, toPx.Y + pad.Y),
                            delayMs: Math.Min(s * 45, 260));
                        state.Parked[idx] = (homePx, to);
                    }
                }
                Services.Log.Info($"make space: cols {tileLeft}-{tileRight} rows {tileTop}-{tileBottom} moved={moves.Count} parked={state.Parked.Count}");
            });
        }

        internal readonly struct ColumnPushResult
        {
            public readonly List<(int idx, (int col, int row) to, int stage)> Placements;
            /// <summary>Icons that had nowhere to go on this monitor (packed solid).</summary>
            public readonly List<int> Overflow;
            public readonly int NextStage;
            public ColumnPushResult(List<(int, (int, int), int)> p, List<int> o, int s) { Placements = p; Overflow = o; NextStage = s; }
            public List<(int idx, (int col, int row) to, int stage)>.Enumerator GetEnumerator() => Placements.GetEnumerator();
        }

        /// <summary>The column-batch push, in isolation from every OS call: given where
        /// icons sit (local, monitor-relative cells) and the tile's blocked column/row
        /// range, decide where each displaced icon goes. Every column the tile covers
        /// gets its occupied rows from the tile's bottom edge down shifted to just past
        /// it, in original top-to-bottom order; whatever doesn't fit carries to the top
        /// of the next column, which shifts its own icons down in turn — the same
        /// down-then-cascade-right the Windows desktop uses, generalised to a tile that
        /// spans more than one column. Icons above the tile, and every column to its
        /// left, are never included in <paramref name="iconCells"/> in the first place
        /// (the caller filters that) and so never move.
        ///
        /// Pure and side-effect-free — no WithListView, no live desktop — so it can be
        /// exercised directly by tests with a synthetic layout.</summary>
        /// <summary>Given each icon's "natural" grid cell, returns a collision-free
        /// mapping: an icon whose cell nobody else claimed keeps it; when two or more
        /// icons compute the SAME natural cell (they started close enough together that
        /// rounding put them there), only the first (by dictionary enumeration order)
        /// keeps it — the rest re-home to the nearest free cell on their own monitor.
        /// Without this, snapping every icon independently to its own nearest cell can
        /// leave two of them landing exactly on top of each other: one icon rendered
        /// over another, with both labels visually interleaved into garbled text.
        /// Pure — no OS calls — so it's directly unit-testable.</summary>
        internal static Dictionary<int, (int col, int row)> ResolveCellCollisions(
            IReadOnlyDictionary<int, (int col, int row)> home, IconGrid grid)
        {
            var claimed = new HashSet<(int, int)>();
            var final = new Dictionary<int, (int col, int row)>();
            var needsNewHome = new List<int>();
            foreach (var kv in home)
            {
                if (claimed.Add(kv.Value)) final[kv.Key] = kv.Value;
                else needsNewHome.Add(kv.Key);
            }
            foreach (var i in needsNewHome)
            {
                var bounds = grid.MonitorRect(home[i].col);
                if (FindFreeCell(grid, home[i], new HashSet<(int, int)>(), claimed, bounds, Rect.Empty) is not { } free)
                    continue; // monitor is completely full; leave this one where it is (still a collision)
                claimed.Add(free);
                final[i] = free;
            }
            return final;
        }

        internal static ColumnPushResult ComputeColumnPush(
            IReadOnlyDictionary<int, (int col, int row)> iconCells,
            int tileLeft, int tileRight, int tileTop, int tileBottom,
            int maxCol, int maxRow)
        {
            // Only icons genuinely INSIDE the blocked band must move — an icon already
            // below the tile, undisturbed, stays exactly where it is unless something
            // else's ripple needs its cell. (The earlier version swept every icon at or
            // below the tile's row into a fresh sequential repack every time, which could
            // touch dozens of untouched icons for a single 2-cell tile placement — the
            // real desktop showed pushes moving 40-60 icons for one drop. This is the fix.)
            var mustMoveByCol = new Dictionary<int, List<int>>(); // tile columns only, original row order
            // Occupancy per column, EXCLUDING must-move icons: row -> icon index. This is
            // the set of gaps/occupants each ripple has to thread through.
            var occByCol = new Dictionary<int, Dictionary<int, int>>();
            foreach (var kv in iconCells)
            {
                int lc = kv.Value.col, r = kv.Value.row;
                if (lc < tileLeft) continue; // left of the tile: never touched
                bool isTileCol = lc >= tileLeft && lc <= tileRight;
                if (isTileCol && r >= tileTop && r <= tileBottom)
                {
                    if (!mustMoveByCol.TryGetValue(lc, out var l)) mustMoveByCol[lc] = l = new List<int>();
                    l.Add(kv.Key);
                }
                else
                {
                    if (!occByCol.TryGetValue(lc, out var d)) occByCol[lc] = d = new Dictionary<int, int>();
                    d[r] = kv.Key;
                }
            }
            foreach (var l in mustMoveByCol.Values) l.Sort((a, b) => iconCells[a].row.CompareTo(iconCells[b].row));

            var carry = new List<int>();
            var moves = new List<(int idx, (int col, int row) to, int stage)>();
            int stage = 0;
            for (int lc = tileLeft; lc <= maxCol; lc++)
            {
                bool isTileCol = lc >= tileLeft && lc <= tileRight;
                if (!occByCol.TryGetValue(lc, out var occ)) occByCol[lc] = occ = new Dictionary<int, int>();

                var incoming = new List<int>(carry); // arrives at the top of this column first
                if (isTileCol && mustMoveByCol.TryGetValue(lc, out var mm)) incoming.AddRange(mm);
                if (incoming.Count == 0) continue; // nothing needs to enter this column: leave it alone entirely

                int searchStart = isTileCol ? tileBottom + 1 : 0;
                var nextCarry = new List<int>();
                // Per-column, overwrite-safe: if icon X gets bumped from row4 to row5 by
                // a LATER incoming icon's chain within this same column, its entry here
                // must be REPLACED (row5), never appended alongside the earlier (stale,
                // row4) one — appending both is what produced two conflicting positions
                // for the same icon (caught by the "clears both" unit test).
                var columnMoves = new Dictionary<int, int>(); // icon -> destination row

                foreach (var idx in incoming)
                {
                    // Walk down from searchStart, threading through whatever's already
                    // occupying each row, until the first genuinely empty cell — the
                    // ripple is exactly as long as it needs to be, no further.
                    var chain = new List<int>();
                    int r = searchStart;
                    bool landed = false;
                    while (r <= maxRow)
                    {
                        chain.Add(r);
                        if (!occ.ContainsKey(r)) { landed = true; break; }
                        r++;
                    }
                    if (!landed) { nextCarry.Add(idx); continue; } // this column is full end to end: cascade right

                    int mover = idx;
                    foreach (var cellRow in chain)
                    {
                        bool wasOccupied = occ.TryGetValue(cellRow, out int occupant);
                        columnMoves[mover] = cellRow;
                        occ[cellRow] = mover;
                        if (!wasOccupied) break; // reached the actual gap; this chain is done
                        mover = occupant;         // the icon that was here rides the chain one step further
                    }
                }

                foreach (var kv in columnMoves) moves.Add((kv.Key, (lc, kv.Value), stage));
                if (columnMoves.Count > 0) stage++;
                carry = nextCarry;
                if (carry.Count == 0 && lc >= tileRight) break; // nothing left to propagate further right
            }

            return new ColumnPushResult(moves, carry, stage);
        }

        /// <summary>The true occupied-cell set after applying <paramref name="moves"/> on
        /// top of <paramref name="before"/>: every icon that didn't move keeps its cell,
        /// every icon that did move counts at its destination. Deliberately NOT computed
        /// as "start from every before-cell, then Remove(before[idx])/Add(to) per move" —
        /// when icon A's destination is icon B's ORIGINAL cell, and B is processed after
        /// A, that Remove(before[B]) wrongly vacates the cell A just moved into, letting
        /// a later placement collide with A. Pure, so it's directly unit-testable.</summary>
        internal static HashSet<(int col, int row)> OccupiedAfterMoves(
            IReadOnlyDictionary<int, (int col, int row)> before,
            IReadOnlyList<(int idx, (int col, int row) to, int stage)> moves)
        {
            var movedIdx = new HashSet<int>();
            foreach (var m in moves) movedIdx.Add(m.idx);

            var occ = new HashSet<(int, int)>();
            foreach (var kv in before)
                if (!movedIdx.Contains(kv.Key)) occ.Add(kv.Value);
            foreach (var m in moves) occ.Add(m.to);
            return occ;
        }

        /// <summary>Median offset of the icons from their cell corners: where the icon
        /// image sits inside its cell.</summary>
        private static Point IconPadding(IconGrid grid, DragDisplacement state) =>
            MedianPhase(grid, state.HomePx.Values);

        /// <summary>Median cell-phase across a set of icon positions: where, typically,
        /// the icon image sits inset within its cell. Robust to a handful of outliers
        /// (parked/off-grid icons) the way an average wouldn't be.</summary>
        private static Point MedianPhase(IconGrid grid, IEnumerable<Point> positions)
        {
            var xs = new List<double>();
            var ys = new List<double>();
            foreach (var p in positions)
            {
                var ph = grid.PhaseOf(p);
                xs.Add(ph.X); ys.Add(ph.Y);
            }
            if (xs.Count == 0) return new Point(0, 0);
            xs.Sort(); ys.Sort();
            return new Point(xs[xs.Count / 2], ys[ys.Count / 2]);
        }

        /// <summary>Slide every displaced icon home (tile moved on, dropped elsewhere,
        /// deleted, or hidden).</summary>
        public static void RestoreDisplacement(DragDisplacement state, Visual? forWindow = null)
        {
            if (!state.Any) return;
            WithListView((lv, proc, remote) =>
            {
                var pad = IconPadding(state.Grid, state); // parked icons sit at cell corner + padding
                int order = 0;
                foreach (var kv in state.Parked)
                {
                    var (home, cell) = kv.Value;
                    var at = state.Grid.CellTopLeft(cell.col, cell.row);
                    IconAnimator.Move(lv, kv.Key, new Point(at.X + pad.X, at.Y + pad.Y), home,
                        delayMs: Math.Min(order++ * 20, 160));
                }
                state.Parked.Clear();
            });
        }

        /// <summary>LVM_GETITEMCOUNT, or -1 if Explorer didn't answer in time.</summary>
        private static int ItemCount(IntPtr lv) =>
            NativeMethods.TrySendMessage(lv, NativeMethods.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero, out var n)
                ? (int)n : -1;

        /// <summary>Raw icon positions in listview-client px (NaN for unreadable items).
        /// Stops early if Explorer stops answering, rather than timing out per icon.</summary>
        private static Point[] ReadPositions(IntPtr lv, IntPtr proc, IntPtr remote, int count)
        {
            var pos = new Point[count];
            var buf = new byte[8];
            for (int i = 0; i < count; i++)
            {
                pos[i] = new Point(double.NaN, double.NaN);
                if (!NativeMethods.TrySendMessage(lv, NativeMethods.LVM_GETITEMPOSITION, (IntPtr)i, remote, out var ok))
                {
                    Services.Log.Error($"Explorer did not answer LVM_GETITEMPOSITION for item {i}; giving up on this read.");
                    for (int j = i + 1; j < count; j++) pos[j] = new Point(double.NaN, double.NaN);
                    break;
                }
                if (ok == IntPtr.Zero) continue;
                if (!NativeMethods.ReadProcessMemory(proc, remote, buf, (IntPtr)8, out _)) continue;
                pos[i] = new Point(BitConverter.ToInt32(buf, 0), BitConverter.ToInt32(buf, 4));
            }
            return pos;
        }

        private static void WithListView(Action<IntPtr, IntPtr, IntPtr> body)
        {
            IntPtr proc = IntPtr.Zero, remote = IntPtr.Zero;
            try
            {
                var lv = ResolveListView();
                if (lv == IntPtr.Zero) return;

                NativeMethods.GetWindowThreadProcessId(lv, out uint pid);
                proc = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_VM_OPERATION | NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_VM_WRITE,
                    false, pid);
                if (proc == IntPtr.Zero) return;
                remote = NativeMethods.VirtualAllocEx(proc, IntPtr.Zero, (IntPtr)8,
                    NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
                if (remote == IntPtr.Zero) return;

                body(lv, proc, remote);
            }
            catch (Exception ex) { Services.Log.Error("WithListView failed", ex); }
            finally
            {
                if (proc != IntPtr.Zero)
                {
                    if (remote != IntPtr.Zero)
                        NativeMethods.VirtualFreeEx(proc, remote, IntPtr.Zero, NativeMethods.MEM_RELEASE);
                    NativeMethods.CloseHandle(proc);
                }
            }
        }

        private static (int col, int row)? FindFreeCell(
            IconGrid g, (int col, int row) from,
            HashSet<(int, int)> reserved, HashSet<(int, int)> occupied,
            Rect workArea, Rect footprint)
        {
            for (int radius = 1; radius <= 25; radius++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue;
                    var c = (from.col + dx, from.row + dy);
                    if (reserved.Contains(c) || occupied.Contains(c)) continue;

                    var rc = g.CellRect(c.Item1, c.Item2);
                    if (rc.Left < workArea.Left - 2 || rc.Top < workArea.Top - 2) continue;
                    if (rc.Right > workArea.Right + 2 || rc.Bottom > workArea.Bottom + 2) continue;
                    if (rc.IntersectsWith(footprint)) continue;
                    return c;
                }
            }
            return null;
        }

        /// <summary>Each desktop icon's cell rectangle, in listview-client px. Empty on failure.</summary>
        public static List<Rect> DesktopIconCells() => DesktopIconCells(GridCellPx());

        private static List<Rect> DesktopIconCells(Size cell)
        {
            var result = new List<Rect>();
            WithListView((lv, proc, remote) =>
            {
                int count = ItemCount(lv);
                if (count <= 0 || count > 5000) return;
                var pos = ReadPositions(lv, proc, remote, count);
                foreach (var p in pos)
                    if (!double.IsNaN(p.X))
                        result.Add(new Rect(p.X, p.Y, cell.Width, cell.Height));
            });
            return result;
        }

        // ---- "Show desktop icons" state -----------------------------------

        private static IntPtr _cachedListView;

        public static bool DesktopIconsVisible()
        {
            try
            {
                var lv = ResolveListView();
                if (lv == IntPtr.Zero) return true;
                return NativeMethods.IsWindowVisible(lv);
            }
            catch { return true; }
        }

        private static IntPtr ResolveListView()
        {
            if (_cachedListView != IntPtr.Zero && NativeMethods.IsWindow(_cachedListView))
                return _cachedListView;

            IntPtr defView = FindDefView();
            if (defView == IntPtr.Zero) return IntPtr.Zero;

            IntPtr list = NativeMethods.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
            if (list == IntPtr.Zero)
                list = FindDescendantByClass(defView, "SysListView32");

            _cachedListView = list;
            return list;
        }

        private static IntPtr FindDefView()
        {
            IntPtr progman = NativeMethods.GetShellWindow();
            if (progman == IntPtr.Zero)
                progman = NativeMethods.FindWindow("Progman", null);

            IntPtr defView = progman == IntPtr.Zero
                ? IntPtr.Zero
                : NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero) return defView;

            // The delegate is held in a local and kept alive past the call: the
            // marshaller does not root it, and a collection mid-enumeration is a crash.
            IntPtr found = IntPtr.Zero;
            NativeMethods.EnumWindowsProc cb = (h, _) =>
            {
                var dv = NativeMethods.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv != IntPtr.Zero) { found = dv; return false; }
                return true;
            };
            NativeMethods.EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return found;
        }

        private static IntPtr FindDescendantByClass(IntPtr root, string className)
        {
            IntPtr found = IntPtr.Zero;
            var buf = new System.Text.StringBuilder(64);
            NativeMethods.EnumWindowsProc cb = (h, _) =>
            {
                buf.Clear();
                NativeMethods.GetClassName(h, buf, buf.Capacity);
                if (buf.ToString() == className) { found = h; return false; }
                return true;
            };
            NativeMethods.EnumChildWindows(root, cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return found;
        }
    }
}

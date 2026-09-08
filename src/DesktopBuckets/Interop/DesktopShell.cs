using System;
using System.Collections.Generic;
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

        /// <summary>Push the icons under <paramref name="footprintPx"/> out of the way the
        /// way a phone home screen does: each one flows forward in the desktop's own
        /// order (down its column, then the top of the next column), and an occupied cell
        /// pushes its icon on in turn, so a dense column ripples by one cell instead of
        /// icons leaping to whatever free cell happens to be nearest. Icons already
        /// parked stay where they are unless their parking spot is now under the tile.</summary>
        public static void MakeSpace(DragDisplacement state, Rect footprintPx)
        {
            if (!state.Captured) BeginDrag(state);
            if (!state.Captured) return;

            WithListView((lv, proc, remote) =>
            {
                if ((NativeMethods.GetWindowLong(lv, NativeMethods.GWL_STYLE) & NativeMethods.LVS_AUTOARRANGE) != 0)
                    return;
                if (!NativeMethods.GetClientRect(lv, out var cr)) return;

                var grid = state.Grid;

                // Cells under the tile. The footprint is inset a little so a tile flush
                // against a cell line doesn't reserve the neighbouring cell too.
                var fpInset = Inset(footprintPx, 4);
                var reserved = new HashSet<(int, int)>();
                var tl = grid.CellOf(new Point(footprintPx.Left, footprintPx.Top));
                var br = grid.CellOf(new Point(footprintPx.Right, footprintPx.Bottom));

                // Icons stay on the tile's monitor: flow wraps within its work area.
                var bounds = grid.MonitorRect(tl.col);
                int maxCol = Math.Max(0, (int)Math.Floor(bounds.Width / grid.CellPx.Width) - 1);
                int maxRow = Math.Max(0, (int)Math.Floor(bounds.Height / grid.CellPx.Height) - 1);
                for (int c = tl.col - 1; c <= br.col + 1; c++)
                for (int r = tl.row - 1; r <= br.row + 1; r++)
                    if (grid.CellRect(c, r).IntersectsWith(fpInset)) reserved.Add((c, r));
                if (reserved.Count == 0 || reserved.Count > 40) return;

                // Where every icon is right now: parked ones at their parking cell, the
                // rest at home. An icon counts as "under the tile" if its actual rect
                // overlaps the footprint, not merely if its rounded cell matches, so a
                // slightly off-grid icon is never left sitting under the tile.
                var occupied = new Dictionary<(int, int), int>();
                var current = new Dictionary<int, (int col, int row)>();
                var toMove = new List<int>();
                foreach (var kv in state.HomePx)
                {
                    int idx = kv.Key;
                    (int col, int row) cell;
                    Point px;
                    if (state.Parked.TryGetValue(idx, out var p))
                    {
                        cell = p.Cell;
                        px = grid.CellTopLeft(cell.col, cell.row);
                    }
                    else
                    {
                        cell = grid.CellOf(kv.Value);
                        px = kv.Value;
                    }
                    current[idx] = cell;
                    var rect = new Rect(px.X, px.Y, grid.CellPx.Width, grid.CellPx.Height);
                    if (reserved.Contains(cell) || Inset(rect, 6).IntersectsWith(fpInset))
                        toMove.Add(idx);
                    else
                        occupied[cell] = idx;
                }
                if (toMove.Count == 0) return;

                // Column-major, top-left first, so the ripple is predictable.
                toMove.Sort((a, b) => current[a].col != current[b].col
                    ? current[a].col.CompareTo(current[b].col)
                    : current[a].row.CompareTo(current[b].row));

                // Each displaced icon is nudged one cell in the direction whose chain
                // reaches a free cell soonest; the icons in that chain each shift by one
                // cell too (a phone-style ripple, but only as long as it has to be).
                // (idx, to, stagger) — stagger grows along a chain so it reads as a ripple.
                var moves = new List<(int idx, (int col, int row) to, int stagger)>();
                var movedThisCall = new HashSet<int>();
                foreach (var idx in toMove)
                {
                    if (movedThisCall.Contains(idx)) continue;
                    var from = current[idx];
                    var chain = ShortestNudge(grid, from, reserved, occupied, bounds, maxCol, maxRow);
                    if (chain == null)
                    {
                        // Boxed in on every side: nearest free cell instead.
                        var occSet = new HashSet<(int, int)>(occupied.Keys);
                        if (FindFreeCell(grid, from, reserved, occSet, bounds, fpInset) is not { } near) continue;
                        moves.Add((idx, near, 0));
                        occupied[near] = idx;
                        movedThisCall.Add(idx);
                        continue;
                    }
                    var (dir, path) = chain.Value;
                    // path = icons along the way (nearest first); the last one lands in the free cell
                    foreach (var (who, at) in path) occupied.Remove(at);
                    moves.Add((idx, (from.col + dir.dc, from.row + dir.dr), 0));
                    occupied[(from.col + dir.dc, from.row + dir.dr)] = idx;
                    movedThisCall.Add(idx);
                    for (int k = 0; k < path.Count; k++)
                    {
                        var (who, at) = path[k];
                        var to = (at.col + dir.dc, at.row + dir.dr);
                        moves.Add((who, to, k + 1));
                        occupied[to] = who;
                        movedThisCall.Add(who);
                    }
                }

                foreach (var (idx, to, stagger) in moves)
                {
                    var homePx = state.Parked.TryGetValue(idx, out var pk) ? pk.Home : state.HomePx[idx];
                    var fromPx = state.Parked.ContainsKey(idx)
                        ? grid.CellTopLeft(current[idx].col, current[idx].row)
                        : state.HomePx[idx];
                    IconAnimator.Move(lv, idx, fromPx, grid.CellTopLeft(to.col, to.row),
                        delayMs: Math.Min(stagger * 35, 240));
                    state.Parked[idx] = (homePx, to);
                    current[idx] = to;
                }
                Services.Log.Info($"make space: reserved={reserved.Count} under={toMove.Count} moved={moves.Count} parked={state.Parked.Count}");
            });
        }

        private const int MaxNudgeChain = 8;

        /// <summary>For an icon at <paramref name="from"/>: the direction (down, right, up,
        /// left) whose straight-line chain of occupied cells reaches a free cell on this
        /// monitor with the fewest icons in between, and those icons in order. A chain
        /// can't pass through the tile or off the monitor. Null when every direction is
        /// blocked or longer than <see cref="MaxNudgeChain"/>.</summary>
        private static ((int dc, int dr) dir, List<(int who, (int col, int row) at)> path)? ShortestNudge(
            IconGrid g, (int col, int row) from,
            HashSet<(int, int)> reserved, Dictionary<(int, int), int> occupied,
            Rect bounds, int maxCol, int maxRow)
        {
            ((int dc, int dr) dir, List<(int, (int, int))> path)? best = null;
            foreach (var dir in new[] { (dc: 0, dr: 1), (dc: 1, dr: 0), (dc: 0, dr: -1), (dc: -1, dr: 0) })
            {
                var path = new List<(int, (int, int))>();
                var cur = from;
                bool valid = false;
                for (int step = 0; step <= MaxNudgeChain; step++)
                {
                    cur = (cur.col + dir.dc, cur.row + dir.dr);
                    int lc = IconGrid.LocalCol(cur.col);
                    if (lc < 0 || lc > maxCol || cur.row < 0 || cur.row > maxRow) break;   // off the monitor
                    if (reserved.Contains(cur)) break;                                     // can't push through the tile
                    if (occupied.TryGetValue(cur, out int who)) { path.Add((who, cur)); continue; }
                    valid = true;                                                          // free cell: chain ends here
                    break;
                }
                if (!valid) continue;
                if (best == null || path.Count < best.Value.path.Count)
                    best = (dir, path);
                if (path.Count == 0) break;   // can't beat a direct hop
            }
            return best;
        }

        /// <summary>Slide every displaced icon home (tile moved on, dropped elsewhere,
        /// deleted, or hidden).</summary>
        public static void RestoreDisplacement(DragDisplacement state, Visual? forWindow = null)
        {
            if (!state.Any) return;
            WithListView((lv, proc, remote) =>
            {
                int order = 0;
                foreach (var kv in state.Parked)
                {
                    var (home, cell) = kv.Value;
                    IconAnimator.Move(lv, kv.Key, state.Grid.CellTopLeft(cell.col, cell.row), home,
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

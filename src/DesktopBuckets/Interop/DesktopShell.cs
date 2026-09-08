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
        /// DIPs. Convert through the window's DPI scale at the edge, not here.</summary>
        public readonly struct IconGrid
        {
            public readonly Point OriginPx;
            public readonly Size CellPx;
            public readonly List<Rect> Icons;

            public IconGrid(Point origin, Size cell, List<Rect> icons)
            {
                OriginPx = origin; CellPx = cell; Icons = icons;
            }

            public bool Valid => CellPx.Width > 4 && CellPx.Height > 4;

            public (int col, int row) CellOf(Point p) => (
                (int)Math.Round((p.X - OriginPx.X) / CellPx.Width),
                (int)Math.Round((p.Y - OriginPx.Y) / CellPx.Height));

            public Point CellTopLeft(int col, int row) =>
                new(OriginPx.X + col * CellPx.Width, OriginPx.Y + row * CellPx.Height);

            public Rect CellRect(int col, int row)
            {
                var tl = CellTopLeft(col, row);
                return new Rect(tl.X, tl.Y, CellPx.Width, CellPx.Height);
            }

            internal bool SameAs(in IconGrid other) =>
                OriginPx == other.OriginPx && CellPx == other.CellPx && Icons.Count == other.Icons.Count;

            /// <summary>Snap a point to the nearest grid cell's top-left (regular lattice).</summary>
            public Point Snap(Point p)
            {
                var c = CellOf(p);
                return CellTopLeft(c.col, c.row);
            }
        }

        // EVERYTHING here works in desktop-listview CLIENT PIXELS — the one frame that
        // icons (raw LVM_GETITEMPOSITION) and the tile (GetWindowRect minus the listview
        // origin) both live in. No WPF DIP, no per-monitor DPI mixing.

        /// <summary>Icon grid cell from the Windows spacing metric, in device pixels
        /// (fallback only; the real pitch is measured from the icons).</summary>
        public static Size GridCellPx()
        {
            int h = 0, v = 0;
            try
            {
                if (!NativeMethods.SystemParametersInfo(NativeMethods.SPI_ICONHORIZONTALSPACING, 0, ref h, 0)) h = 0;
                if (!NativeMethods.SystemParametersInfo(NativeMethods.SPI_ICONVERTICALSPACING, 0, ref v, 0)) v = 0;
            }
            catch { }
            if (h <= 0) h = 90;
            if (v <= 0) v = 110;
            return new Size(h, v);
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
            var icons = DesktopIconCells();
            var spi = GridCellPx();

            if (icons.Count == 0)
                return new IconGrid(new Point(0, 0), spi, icons);

            var cols = ClusterAxis(icons.ConvertAll(r => r.X), 24);
            var rows = ClusterAxis(icons.ConvertAll(r => r.Y), 24);

            double cw = MedianGap(cols) ?? spi.Width;
            double ch = MedianGap(rows) ?? spi.Height;
            cw = Clamp(cw, spi.Width * 0.55, spi.Width * 3.0);
            ch = Clamp(ch, spi.Height * 0.55, spi.Height * 3.0);

            // Origin = the grid PHASE most icons share, not one corner icon (which may be
            // a stray at an odd position that would shift every snap). This keeps snapping
            // aligned even when the desktop is a bit messy.
            double ox = ModalPhase(icons.ConvertAll(r => r.X), cw, cols[0]);
            double oy = ModalPhase(icons.ConvertAll(r => r.Y), ch, rows[0]);

            var grid = new IconGrid(new Point(ox, oy), new Size(cw, ch), icons);
            // Log only when the answer changes — this used to be a locked file append
            // on the UI thread for every refresh.
            if (!_loggedOnce || !grid.SameAs(_lastLoggedGrid))
            {
                Services.Log.Info($"IconGrid(px): origin=({ox:F0},{oy:F0}) cell={cw:F0}x{ch:F0} " +
                                  $"cols={cols.Count} rows={rows.Count} icons={icons.Count}");
                _lastLoggedGrid = grid;
                _loggedOnce = true;
            }
            return grid;
        }

        private static System.Collections.Generic.List<double> ClusterAxis(
            System.Collections.Generic.List<double> values, double tolerance)
        {
            var sorted = new System.Collections.Generic.List<double>(values);
            sorted.Sort();
            var clusters = new System.Collections.Generic.List<double>();
            foreach (var v in sorted)
            {
                // A cluster is anchored to its first (smallest) member — icons in a
                // column share an X, so the first is the true line; averaging drifts it.
                if (clusters.Count == 0 || v - clusters[^1] > tolerance)
                    clusters.Add(v);
            }
            return clusters;
        }

        /// <summary>Median of adjacent-line gaps above a floor — robust to empty
        /// columns/rows (which show up as 2×,3× gaps) and accidental tight pairs.</summary>
        private static double? MedianGap(System.Collections.Generic.List<double> lines)
        {
            var gaps = new System.Collections.Generic.List<double>();
            for (int i = 1; i < lines.Count; i++)
            {
                double g = lines[i] - lines[i - 1];
                if (g > 12) gaps.Add(g);
            }
            if (gaps.Count == 0) return null;
            gaps.Sort();

            // Empty rows/cols create 2×/3× gaps; fold them down to the base pitch by
            // taking the smallest cluster of gaps (those within 1.5× of the minimum).
            double min = gaps[0];
            var baseGaps = gaps.FindAll(g => g <= min * 1.5);
            return baseGaps[baseGaps.Count / 2];
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>The lattice phase (0..cell) most icon coordinates share, returned as an
        /// origin near <paramref name="near"/>. Robust to a few off-grid stray icons.</summary>
        private static double ModalPhase(System.Collections.Generic.List<double> values, double cell, double near)
        {
            if (cell <= 1 || values.Count == 0) return near;
            var buckets = new System.Collections.Generic.Dictionary<int, int>();
            foreach (var v in values)
            {
                int ph = (int)Math.Round(((v % cell) + cell) % cell);
                buckets.TryGetValue(ph, out int n);
                buckets[ph] = n + 1;
            }
            int bestPh = 0, best = -1;
            foreach (var kv in buckets)
                if (kv.Value > best) { best = kv.Value; bestPh = kv.Key; }
            // slide bestPh to the multiple of cell nearest the top-left-most icon line
            double k = Math.Round((near - bestPh) / cell);
            return bestPh + k * cell;
        }

        private static Rect Inset(Rect r, double d) =>
            new(r.X + d, r.Y + d, Math.Max(1, r.Width - 2 * d), Math.Max(1, r.Height - 2 * d));

        // ---- live displacement while dragging a tile --------------------

        /// <summary>Tracks a tile drag's effect on the desktop icons. Captures a STABLE
        /// snapshot at drag start — the grid and every icon's home cell — so recomputing
        /// as icons animate can't drift. Icons under the tile are "parked" elsewhere and
        /// slid home when the tile moves off, or fully restored when it goes away.</summary>
        public sealed class DragDisplacement
        {
            internal IconGrid Grid;
            internal bool Captured;
            // per icon index: its untouched home cell (never mutated during the drag)
            internal readonly System.Collections.Generic.Dictionary<int, (int col, int row)> HomeCell = new();
            // icons currently parked: index -> (homeDip, parkedCell)
            internal readonly System.Collections.Generic.Dictionary<int, (Point Home, (int col, int row) Cell)> Parked = new();
            public bool Any => Parked.Count > 0;

            public void Reset() { Captured = false; HomeCell.Clear(); Parked.Clear(); }
        }

        /// <summary>Prepare for a (possibly repeat) drag. Captures the grid once, and
        /// refreshes each icon's TRUE home cell from its current resting position —
        /// EXCEPT icons still parked from a previous drag, whose recorded home is kept
        /// so they can still return there. Never clears the parked set.</summary>
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
                    if (!double.IsNaN(pos0[i].X))
                        state.HomeCell[i] = grid0.CellOf(pos0[i]);
                }
                state.Captured = true;
            });
            return state.Grid;
        }

        /// <summary>Recompute displacement for the tile's client-px footprint against the
        /// snapshot grid: park icons now under it, un-park icons whose home is clear again.</summary>
        public static void UpdateDragDisplace(DragDisplacement state, Rect footprintPx, Visual? forWindow = null)
        {
            if (!state.Captured) BeginDrag(state);
            if (!state.Captured) return;

            WithListView((lv, proc, remote) =>
            {
                if ((NativeMethods.GetWindowLong(lv, NativeMethods.GWL_STYLE) & NativeMethods.LVS_AUTOARRANGE) != 0)
                    return;

                // Bounds for parking icons = the listview's own client area, in the same
                // px frame as the cells. (SystemParameters.WorkArea is primary-monitor
                // DIPs; at 150% it rejected every cell in the right/bottom third.)
                if (!NativeMethods.GetClientRect(lv, out var cr)) return;
                var bounds = new Rect(0, 0, cr.Right - cr.Left, cr.Bottom - cr.Top);

                var grid = state.Grid;
                var fpInset = Inset(footprintPx, 4);
                var reserved = new HashSet<(int, int)>();
                var tl = grid.CellOf(new Point(footprintPx.Left, footprintPx.Top));
                var brc = grid.CellOf(new Point(footprintPx.Right, footprintPx.Bottom));
                for (int c = tl.col - 1; c <= brc.col + 1; c++)
                for (int r = tl.row - 1; r <= brc.row + 1; r++)
                    if (grid.CellRect(c, r).IntersectsWith(fpInset)) reserved.Add((c, r));
                if (reserved.Count == 0 || reserved.Count > 40) return;

                var occupied = new HashSet<(int, int)>();
                foreach (var kv in state.HomeCell)
                    if (!state.Parked.ContainsKey(kv.Key) && !reserved.Contains(kv.Value))
                        occupied.Add(kv.Value);
                foreach (var kv in state.Parked)
                    occupied.Add(kv.Value.Cell);

                int unparked = 0, parked = 0;

                // Un-park icons whose home cell is clear again.
                foreach (var idx in new System.Collections.Generic.List<int>(state.Parked.Keys))
                {
                    var home = state.HomeCell[idx];
                    if (!reserved.Contains(home))
                    {
                        var it = state.Parked[idx];
                        IconAnimator.Move(lv, idx, grid.CellTopLeft(it.Cell.col, it.Cell.row), it.Home);
                        occupied.Remove(it.Cell);
                        occupied.Add(home);
                        state.Parked.Remove(idx);
                        unparked++;
                    }
                }

                // Park icons whose home cell is now under the tile.
                foreach (var kv in state.HomeCell)
                {
                    int idx = kv.Key;
                    var home = kv.Value;
                    if (state.Parked.ContainsKey(idx) || !reserved.Contains(home)) continue;
                    if (state.Parked.Count >= 30) break;

                    var free = FindFreeCell(grid, home, reserved, occupied, bounds, fpInset);
                    if (free is not { } f) continue;

                    var homePx = grid.CellTopLeft(home.col, home.row);
                    IconAnimator.Move(lv, idx, homePx, grid.CellTopLeft(f.col, f.row));
                    state.Parked[idx] = (homePx, f);
                    occupied.Remove(home);
                    occupied.Add(f);
                    parked++;
                }
                if (parked > 0 || unparked > 0)
                    Services.Log.Info($"displace: reserved={reserved.Count} +parked={parked} -unparked={unparked} total={state.Parked.Count}");
            });
        }

        /// <summary>Slide every displaced icon home (tile deleted / hidden / drag cancelled).</summary>
        public static void RestoreDisplacement(DragDisplacement state, Visual? forWindow = null)
        {
            if (!state.Any) return;
            WithListView((lv, proc, remote) =>
            {
                foreach (var kv in state.Parked)
                {
                    var (home, cell) = kv.Value;
                    IconAnimator.Move(lv, kv.Key, state.Grid.CellTopLeft(cell.col, cell.row), home);
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
        public static List<Rect> DesktopIconCells()
        {
            var result = new List<Rect>();
            var cell = GridCellPx();
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

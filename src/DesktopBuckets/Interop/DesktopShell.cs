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
        /// <summary>The desktop icon grid, in device-independent units.</summary>
        public readonly struct IconGrid
        {
            public readonly Point OriginDip;
            public readonly Size CellDip;
            public readonly List<Rect> Icons;

            public IconGrid(Point origin, Size cell, List<Rect> icons)
            {
                OriginDip = origin; CellDip = cell; Icons = icons;
            }

            public bool Valid => CellDip.Width > 4 && CellDip.Height > 4;

            public (int col, int row) CellOf(Point p) => (
                (int)Math.Round((p.X - OriginDip.X) / CellDip.Width),
                (int)Math.Round((p.Y - OriginDip.Y) / CellDip.Height));

            public Point CellTopLeft(int col, int row) =>
                new(OriginDip.X + col * CellDip.Width, OriginDip.Y + row * CellDip.Height);

            public Rect CellRect(int col, int row)
            {
                var tl = CellTopLeft(col, row);
                return new Rect(tl.X, tl.Y, CellDip.Width, CellDip.Height);
            }
        }

        /// <summary>Icon grid cell from the Windows spacing metric, DIP-scaled.</summary>
        public static Size GridCellDip(Visual forWindow)
        {
            int h = 0, v = 0;
            try
            {
                if (!NativeMethods.SystemParametersInfo(NativeMethods.SPI_ICONHORIZONTALSPACING, 0, ref h, 0)) h = 0;
                if (!NativeMethods.SystemParametersInfo(NativeMethods.SPI_ICONVERTICALSPACING, 0, ref v, 0)) v = 0;
            }
            catch { /* fallback */ }

            if (h <= 0) h = 76;
            if (v <= 0) v = 102;

            double sx = 1, sy = 1;
            try { var dpi = VisualTreeHelper.GetDpi(forWindow); sx = dpi.DpiScaleX; sy = dpi.DpiScaleY; }
            catch { }

            return new Size(h / sx, v / sy);
        }

        /// <summary>Real desktop grid, measured from the actual icons: origin = the
        /// first column/row line, cell = the real pitch between lines (Windows spacing
        /// metric only as a fallback).</summary>
        public static IconGrid GetIconGrid(Visual forWindow)
        {
            var icons = DesktopIconCellsDip(forWindow);
            var spi = GridCellDip(forWindow);

            if (icons.Count == 0)
            {
                var wa = SystemParameters.WorkArea;
                return new IconGrid(new Point(wa.Left + 8, wa.Top + 8), spi, icons);
            }

            var cols = ClusterAxis(icons.ConvertAll(r => r.X), 24);
            var rows = ClusterAxis(icons.ConvertAll(r => r.Y), 24);

            double cw = MinGap(cols) ?? spi.Width;
            double ch = MinGap(rows) ?? spi.Height;
            cw = Clamp(cw, spi.Width * 0.55, spi.Width * 2.2);
            ch = Clamp(ch, spi.Height * 0.55, spi.Height * 2.2);

            return new IconGrid(new Point(cols[0], rows[0]), new Size(cw, ch), icons);
        }

        /// <summary>Snaps to the nearest actual icon column/row line, extrapolating by
        /// the measured pitch when the tile is dragged beyond the icon block.</summary>
        public static Point SnapToIconLattice(Point topLeft, Visual forWindow)
        {
            var grid = GetIconGrid(forWindow);
            if (!grid.Valid) return topLeft;

            var cols = grid.Icons.Count > 0 ? ClusterAxis(grid.Icons.ConvertAll(r => r.X), 24) : null;
            var rows = grid.Icons.Count > 0 ? ClusterAxis(grid.Icons.ConvertAll(r => r.Y), 24) : null;

            double x = SnapAxis(topLeft.X, cols, grid.CellDip.Width);
            double y = SnapAxis(topLeft.Y, rows, grid.CellDip.Height);
            return new Point(x, y);
        }

        private static double SnapAxis(double v, System.Collections.Generic.List<double>? lines, double pitch)
        {
            if (lines == null || lines.Count == 0)
                return v; // no reference — leave as-is

            double nearest = lines[0], best = Math.Abs(v - lines[0]);
            foreach (var a in lines)
            {
                double d = Math.Abs(v - a);
                if (d < best) { best = d; nearest = a; }
            }
            if (best <= pitch * 0.75) return nearest;

            double edge = v < lines[0] ? lines[0] : lines[^1];
            return edge + Math.Round((v - edge) / pitch) * pitch;
        }

        private static System.Collections.Generic.List<double> ClusterAxis(
            System.Collections.Generic.List<double> values, double tolerance)
        {
            var sorted = new System.Collections.Generic.List<double>(values);
            sorted.Sort();
            var clusters = new System.Collections.Generic.List<double>();
            foreach (var v in sorted)
            {
                if (clusters.Count == 0 || v - clusters[^1] > tolerance)
                    clusters.Add(v);
                else
                    clusters[^1] = (clusters[^1] + v) / 2;
            }
            return clusters;
        }

        private static double? MinGap(System.Collections.Generic.List<double> lines)
        {
            double? min = null;
            for (int i = 1; i < lines.Count; i++)
            {
                double g = lines[i] - lines[i - 1];
                if (g > 12 && (min == null || g < min)) min = g;
            }
            return min;
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>Moves any desktop icons under <paramref name="tileFootprintDip"/> out
        /// to the nearest free grid cells, so the tile sits in a clean gap. No-op if the
        /// desktop uses auto-arrange (positions wouldn't stick) or the view can't be read.</summary>
        public static void ClaimSpace(Rect tileFootprintDip, Visual forWindow)
        {
            IntPtr proc = IntPtr.Zero, remote = IntPtr.Zero;
            try
            {
                var lv = ResolveListView();
                if (lv == IntPtr.Zero) return;

                int style = NativeMethods.GetWindowLong(lv, NativeMethods.GWL_STYLE);
                if ((style & NativeMethods.LVS_AUTOARRANGE) != 0)
                {
                    Services.Log.Info("Desktop 'Auto arrange icons' is on — not displacing icons.");
                    return;
                }

                if (!NativeMethods.GetWindowRect(lv, out var lvRect)) return;
                int count = (int)NativeMethods.SendMessage(lv, NativeMethods.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
                if (count <= 0 || count > 5000) return;

                NativeMethods.GetWindowThreadProcessId(lv, out uint pid);
                proc = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_VM_OPERATION | NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_VM_WRITE,
                    false, pid);
                if (proc == IntPtr.Zero) return;

                remote = NativeMethods.VirtualAllocEx(proc, IntPtr.Zero, (IntPtr)8,
                    NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
                if (remote == IntPtr.Zero) return;

                var dpi = VisualTreeHelper.GetDpi(forWindow);
                double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
                var grid = GetIconGrid(forWindow);
                if (!grid.Valid) return;

                var posDip = new Point[count];
                var buf = new byte[8];
                for (int i = 0; i < count; i++)
                {
                    posDip[i] = new Point(double.NaN, double.NaN);
                    if (NativeMethods.SendMessage(lv, NativeMethods.LVM_GETITEMPOSITION, (IntPtr)i, remote) == IntPtr.Zero) continue;
                    if (!NativeMethods.ReadProcessMemory(proc, remote, buf, (IntPtr)8, out _)) continue;
                    int x = BitConverter.ToInt32(buf, 0), y = BitConverter.ToInt32(buf, 4);
                    posDip[i] = new Point((lvRect.Left + x) / sx, (lvRect.Top + y) / sy);
                }

                var fpInset = Inset(tileFootprintDip, 3);

                var reserved = new HashSet<(int, int)>();
                var tl = grid.CellOf(new Point(tileFootprintDip.Left, tileFootprintDip.Top));
                var br = grid.CellOf(new Point(tileFootprintDip.Right, tileFootprintDip.Bottom));
                for (int col = tl.col - 1; col <= br.col + 1; col++)
                for (int row = tl.row - 1; row <= br.row + 1; row++)
                    if (grid.CellRect(col, row).IntersectsWith(fpInset))
                        reserved.Add((col, row));

                // Sanity: a normal tile spans a handful of cells. A huge count means the
                // grid math is off — don't rearrange the desktop on a bad reading.
                if (reserved.Count == 0 || reserved.Count > 30) return;

                var occupied = new HashSet<(int, int)>();
                for (int i = 0; i < count; i++)
                {
                    if (double.IsNaN(posDip[i].X)) continue;
                    var c = grid.CellOf(posDip[i]);
                    if (!reserved.Contains(c)) occupied.Add(c);
                }

                var wa = SystemParameters.WorkArea;
                int moved = 0;
                var wb = new byte[8];

                for (int i = 0; i < count && moved < 16; i++)
                {
                    if (double.IsNaN(posDip[i].X)) continue;
                    var cur = grid.CellOf(posDip[i]);
                    if (!reserved.Contains(cur)) continue;

                    var target = FindFreeCell(grid, cur, reserved, occupied, wa, fpInset);
                    if (target is not { } t) continue;
                    occupied.Add(t);

                    var dst = grid.CellTopLeft(t.col, t.row);
                    int px = (int)Math.Round(dst.X * sx - lvRect.Left);
                    int py = (int)Math.Round(dst.Y * sy - lvRect.Top);
                    BitConverter.GetBytes(px).CopyTo(wb, 0);
                    BitConverter.GetBytes(py).CopyTo(wb, 4);
                    if (NativeMethods.WriteProcessMemory(proc, remote, wb, (IntPtr)8, out _))
                    {
                        NativeMethods.SendMessage(lv, NativeMethods.LVM_SETITEMPOSITION32, (IntPtr)i, remote);
                        moved++;
                    }
                }

                if (moved > 0)
                    Services.Log.Info($"ClaimSpace: pushed {moved} desktop icon(s) clear of the bucket.");
            }
            catch (Exception ex) { Services.Log.Error("ClaimSpace failed", ex); }
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

        private static Rect Inset(Rect r, double d) =>
            new(r.X + d, r.Y + d, Math.Max(1, r.Width - 2 * d), Math.Max(1, r.Height - 2 * d));

        // ---- live displacement while dragging a tile --------------------

        /// <summary>Tracks which desktop icons a tile drag has pushed aside, so they can
        /// slide back when the tile no longer covers their home cell, and be fully
        /// restored when the bucket goes away.</summary>
        public sealed class DragDisplacement
        {
            internal readonly System.Collections.Generic.Dictionary<int, (Point Home, Point Parked)> Items = new();
            public bool Any => Items.Count > 0;
        }

        /// <summary>Recompute displacement for the tile's current footprint: park icons
        /// that just came under it, un-park icons whose home is now clear. Animated.</summary>
        public static void UpdateDragDisplace(DragDisplacement state, Rect footprintDip, Visual forWindow)
        {
            WithListView(forWindow, (lv, proc, remote, lvRect, sx, sy) =>
            {
                if ((NativeMethods.GetWindowLong(lv, NativeMethods.GWL_STYLE) & NativeMethods.LVS_AUTOARRANGE) != 0)
                    return;

                int count = (int)NativeMethods.SendMessage(lv, NativeMethods.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
                if (count <= 0 || count > 5000) return;

                var pos = ReadPositions(lv, proc, remote, count, lvRect, sx, sy);
                var grid = GetIconGrid(forWindow);
                if (!grid.Valid) return;

                var fpInset = Inset(footprintDip, 3);
                var reserved = new HashSet<(int, int)>();
                var tl = grid.CellOf(new Point(footprintDip.Left, footprintDip.Top));
                var brc = grid.CellOf(new Point(footprintDip.Right, footprintDip.Bottom));
                for (int c = tl.col - 1; c <= brc.col + 1; c++)
                for (int r = tl.row - 1; r <= brc.row + 1; r++)
                    if (grid.CellRect(c, r).IntersectsWith(fpInset)) reserved.Add((c, r));
                if (reserved.Count > 40) return;

                var occupied = new HashSet<(int, int)>();
                for (int i = 0; i < count; i++)
                    if (!double.IsNaN(pos[i].X)) occupied.Add(grid.CellOf(pos[i]));

                // un-park: home cell is clear again
                foreach (var idx in new System.Collections.Generic.List<int>(state.Items.Keys))
                {
                    var it = state.Items[idx];
                    if (!reserved.Contains(grid.CellOf(it.Home)))
                    {
                        IconAnimator.Move(lv, idx, it.Parked, it.Home, sx, sy, lvRect);
                        occupied.Remove(grid.CellOf(it.Parked));
                        occupied.Add(grid.CellOf(it.Home));
                        state.Items.Remove(idx);
                    }
                }

                // park: newly under the footprint
                for (int i = 0; i < count && state.Items.Count < 24; i++)
                {
                    if (double.IsNaN(pos[i].X) || state.Items.ContainsKey(i)) continue;
                    var cell = grid.CellOf(pos[i]);
                    if (!reserved.Contains(cell)) continue;

                    var free = FindFreeCell(grid, cell, reserved, occupied, SystemParameters.WorkArea, fpInset);
                    if (free is not { } f) continue;

                    var home = grid.CellTopLeft(cell.col, cell.row);
                    var parked = grid.CellTopLeft(f.col, f.row);
                    IconAnimator.Move(lv, i, pos[i], parked, sx, sy, lvRect);
                    state.Items[i] = (home, parked);
                    occupied.Remove(cell);
                    occupied.Add(f);
                }
            });
        }

        /// <summary>Slide every displaced icon home (used when the tile is deleted or hidden).</summary>
        public static void RestoreDisplacement(DragDisplacement state, Visual forWindow)
        {
            if (!state.Any) return;
            WithListView(forWindow, (lv, proc, remote, lvRect, sx, sy) =>
            {
                foreach (var kv in state.Items)
                    IconAnimator.Move(lv, kv.Key, kv.Value.Parked, kv.Value.Home, sx, sy, lvRect);
                state.Items.Clear();
            });
        }

        private static Point[] ReadPositions(IntPtr lv, IntPtr proc, IntPtr remote, int count,
            NativeMethods.RECT lvRect, double sx, double sy)
        {
            var pos = new Point[count];
            var buf = new byte[8];
            for (int i = 0; i < count; i++)
            {
                pos[i] = new Point(double.NaN, double.NaN);
                if (NativeMethods.SendMessage(lv, NativeMethods.LVM_GETITEMPOSITION, (IntPtr)i, remote) == IntPtr.Zero) continue;
                if (!NativeMethods.ReadProcessMemory(proc, remote, buf, (IntPtr)8, out _)) continue;
                pos[i] = new Point((lvRect.Left + BitConverter.ToInt32(buf, 0)) / sx,
                                   (lvRect.Top + BitConverter.ToInt32(buf, 4)) / sy);
            }
            return pos;
        }

        private static void WithListView(Visual forWindow,
            Action<IntPtr, IntPtr, IntPtr, NativeMethods.RECT, double, double> body)
        {
            IntPtr proc = IntPtr.Zero, remote = IntPtr.Zero;
            try
            {
                var lv = ResolveListView();
                if (lv == IntPtr.Zero) return;
                if (!NativeMethods.GetWindowRect(lv, out var lvRect)) return;

                NativeMethods.GetWindowThreadProcessId(lv, out uint pid);
                proc = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_VM_OPERATION | NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_VM_WRITE,
                    false, pid);
                if (proc == IntPtr.Zero) return;
                remote = NativeMethods.VirtualAllocEx(proc, IntPtr.Zero, (IntPtr)8,
                    NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
                if (remote == IntPtr.Zero) return;

                var dpi = VisualTreeHelper.GetDpi(forWindow);
                body(lv, proc, remote, lvRect, dpi.DpiScaleX, dpi.DpiScaleY);
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

        /// <summary>Each desktop icon's cell rectangle, in device-independent (screen) units.
        /// Empty on any failure.</summary>
        public static List<Rect> DesktopIconCellsDip(Visual forWindow)
        {
            var result = new List<Rect>();
            IntPtr proc = IntPtr.Zero, remote = IntPtr.Zero;
            try
            {
                var lv = ResolveListView();
                if (lv == IntPtr.Zero) return result;
                if (!NativeMethods.GetWindowRect(lv, out var lvRect)) return result;

                int count = (int)NativeMethods.SendMessage(lv, NativeMethods.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
                if (count <= 0 || count > 5000) return result;

                NativeMethods.GetWindowThreadProcessId(lv, out uint pid);
                proc = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_VM_OPERATION | NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_VM_WRITE,
                    false, pid);
                if (proc == IntPtr.Zero) return result;

                remote = NativeMethods.VirtualAllocEx(proc, IntPtr.Zero, (IntPtr)8,
                    NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
                if (remote == IntPtr.Zero) return result;

                var dpi = VisualTreeHelper.GetDpi(forWindow);
                var cell = GridCellDip(forWindow);
                var buf = new byte[8];

                for (int i = 0; i < count; i++)
                {
                    if (NativeMethods.SendMessage(lv, NativeMethods.LVM_GETITEMPOSITION, (IntPtr)i, remote) == IntPtr.Zero)
                        continue;
                    if (!NativeMethods.ReadProcessMemory(proc, remote, buf, (IntPtr)8, out _))
                        continue;

                    int x = BitConverter.ToInt32(buf, 0);
                    int y = BitConverter.ToInt32(buf, 4);
                    double px = (lvRect.Left + x) / dpi.DpiScaleX;
                    double py = (lvRect.Top + y) / dpi.DpiScaleY;
                    result.Add(new Rect(px, py, cell.Width, cell.Height));
                }
            }
            catch { result.Clear(); }
            finally
            {
                if (proc != IntPtr.Zero)
                {
                    if (remote != IntPtr.Zero)
                        NativeMethods.VirtualFreeEx(proc, remote, IntPtr.Zero, NativeMethods.MEM_RELEASE);
                    NativeMethods.CloseHandle(proc);
                }
            }
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

            IntPtr found = IntPtr.Zero;
            NativeMethods.EnumWindows((h, _) =>
            {
                var dv = NativeMethods.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv != IntPtr.Zero) { found = dv; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static IntPtr FindDescendantByClass(IntPtr root, string className)
        {
            IntPtr found = IntPtr.Zero;
            var buf = new System.Text.StringBuilder(64);
            NativeMethods.EnumChildWindows(root, (h, _) =>
            {
                buf.Clear();
                NativeMethods.GetClassName(h, buf, buf.Capacity);
                if (buf.ToString() == className) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }
    }
}

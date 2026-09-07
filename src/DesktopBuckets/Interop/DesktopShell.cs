using System;
using System.Windows;
using System.Windows.Media;

namespace DesktopBuckets.Interop
{
    /// <summary>
    /// Ties tiles to the real desktop: the icon-grid cell size (for snapping) and
    /// whether "Show desktop icons" is currently on (so tiles can follow it).
    /// Every method is best-effort and never throws.
    /// </summary>
    internal static class DesktopShell
    {
        /// <summary>Desktop icon grid cell, in device-independent units, for the given window.
        /// Falls back to a sane Windows 11 default if the metrics can't be read.</summary>
        public static Size GridCellDip(Visual forWindow)
        {
            int h = 0, v = 0;
            try
            {
                if (!NativeMethods.SystemParametersInfo(NativeMethods.SPI_ICONHORIZONTALSPACING, 0, ref h, 0)) h = 0;
                if (!NativeMethods.SystemParametersInfo(NativeMethods.SPI_ICONVERTICALSPACING, 0, ref v, 0)) v = 0;
            }
            catch { /* use fallback */ }

            if (h <= 0) h = 76;
            if (v <= 0) v = 102;

            double sx = 1, sy = 1;
            try { var dpi = VisualTreeHelper.GetDpi(forWindow); sx = dpi.DpiScaleX; sy = dpi.DpiScaleY; }
            catch { /* assume 1.0 */ }

            return new Size(h / sx, v / sy);
        }

        /// <summary>Snaps a top-left point to the desktop grid, using the primary work
        /// area's top-left (plus a small inset matching where Explorer starts icons) as origin.</summary>
        public static Point SnapToGrid(Point topLeft, Size cell)
        {
            if (cell.Width <= 0 || cell.Height <= 0) return topLeft;

            var wa = SystemParameters.WorkArea;
            double ox = wa.Left + 8;
            double oy = wa.Top + 8;

            double sx = ox + Math.Round((topLeft.X - ox) / cell.Width) * cell.Width;
            double sy = oy + Math.Round((topLeft.Y - oy) / cell.Height) * cell.Height;
            return new Point(sx, sy);
        }

        /// <summary>Snaps to the grid, then — if the tile's footprint would land on any
        /// desktop icons — walks outward, cell by cell, to the nearest grid slot that is
        /// clear of icons. Icons are never moved. Falls back to the plain snap if the icon
        /// positions can't be read or nothing is free nearby.</summary>
        public static Point SnapAvoidingIcons(Point desiredTopLeft, Size tileDip, Size cell, Visual forWindow)
        {
            var basePt = SnapToGrid(desiredTopLeft, cell);
            if (cell.Width <= 0 || cell.Height <= 0) return basePt;

            var icons = DesktopIconCellsDip(forWindow);
            if (icons.Count == 0) return basePt;

            Rect Footprint(Point p) => new Rect(
                p.X + 2, p.Y + 2,
                Math.Max(1, tileDip.Width - 4), Math.Max(1, tileDip.Height - 4));

            bool Clear(Point p)
            {
                var fp = Footprint(p);
                foreach (var ic in icons)
                    if (fp.IntersectsWith(ic)) return false;
                return true;
            }

            if (Clear(basePt)) return basePt;

            var wa = SystemParameters.WorkArea;
            for (int radius = 1; radius <= 15; radius++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue; // ring only
                    var cand = new Point(basePt.X + dx * cell.Width, basePt.Y + dy * cell.Height);
                    if (cand.X < wa.Left - 2 || cand.Y < wa.Top - 2) continue;
                    if (cand.X + tileDip.Width > wa.Right + 2) continue;
                    if (cand.Y + tileDip.Height > wa.Bottom + 2) continue;
                    if (Clear(cand)) return cand;
                }
            }
            return basePt;
        }

        /// <summary>Each desktop icon's cell rectangle, in device-independent units
        /// (screen coordinates). Empty on any failure.</summary>
        public static System.Collections.Generic.List<Rect> DesktopIconCellsDip(Visual forWindow)
        {
            var result = new System.Collections.Generic.List<Rect>();
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
            catch
            {
                result.Clear();
            }
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

        /// <summary>True when the desktop icon list view is visible, i.e. the user has
        /// "Show desktop icons" checked. Also true if the view can't be located (fail safe).</summary>
        public static bool DesktopIconsVisible()
        {
            try
            {
                var lv = ResolveListView();
                if (lv == IntPtr.Zero) return true;
                return NativeMethods.IsWindowVisible(lv);
            }
            catch
            {
                return true;
            }
        }

        private static IntPtr ResolveListView()
        {
            if (_cachedListView != IntPtr.Zero && NativeMethods.IsWindow(_cachedListView))
                return _cachedListView;

            IntPtr defView = FindDefView();
            if (defView == IntPtr.Zero) return IntPtr.Zero;

            // On classic Windows SysListView32 is a direct child of SHELLDLL_DefView;
            // on some Windows 11 builds it sits one level deeper, so search descendants.
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

            // Wallpaper slideshow / some GPUs park the view under a WorkerW.
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

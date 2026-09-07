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

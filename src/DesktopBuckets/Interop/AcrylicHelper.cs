using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopBuckets.Interop
{
    /// <summary>
    /// Gives a layered (AllowsTransparency=True) borderless window a frosted-glass
    /// blur-behind with a dark tint, and clips it to rounded corners. Best-effort.
    /// </summary>
    internal static class AcrylicHelper
    {
        public static void Apply(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            // Classic Aero blur-behind: reliably blurs on Win10/11. Recent builds
            // ignore custom colours on the "acrylic" variant, so the dark tint lives
            // in the window's own fill (BucketTileWindow.xaml) instead.
            if (!TrySetAccent(hwnd, NativeMethods.AccentState.ACCENT_ENABLE_BLURBEHIND, 0))
                TrySetAccent(hwnd, NativeMethods.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND, 0);

            ApplyRoundedRegion(window, 12);
        }

        /// <summary>Clips the window (and therefore the blur) to a rounded rectangle.
        /// Call again whenever the window resizes.</summary>
        public static void ApplyRoundedRegion(Window window, double cornerRadiusDip)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                var src = HwndSource.FromHwnd(hwnd);
                var m = src?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
                double sx = m.M11 == 0 ? 1 : m.M11;
                double sy = m.M22 == 0 ? 1 : m.M22;

                int w = (int)Math.Round(window.ActualWidth * sx);
                int h = (int)Math.Round(window.ActualHeight * sy);
                if (w <= 0 || h <= 0) return;

                int r = (int)Math.Round(cornerRadiusDip * sx) * 2;
                IntPtr rgn = NativeMethods.CreateRoundRectRgn(0, 0, w + 1, h + 1, r, r);
                if (rgn != IntPtr.Zero)
                {
                    // Window takes ownership of the region; do not DeleteObject it.
                    if (NativeMethods.SetWindowRgn(hwnd, rgn, true) == 0)
                        NativeMethods.DeleteObject(rgn);
                }
            }
            catch (Exception ex) { Services.Log.Error("ApplyRoundedRegion failed", ex); }
        }

        private static bool TrySetAccent(IntPtr hwnd, NativeMethods.AccentState state, uint gradientColor)
        {
            try
            {
                var accent = new NativeMethods.AccentPolicy
                {
                    AccentState = state,
                    AccentFlags = 0, // the Border draws our edge; no system border
                    GradientColor = gradientColor,
                };
                int size = Marshal.SizeOf(accent);
                IntPtr ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(accent, ptr, false);
                    var data = new NativeMethods.WindowCompositionAttributeData
                    {
                        Attribute = 19, // WCA_ACCENT_POLICY
                        Data = ptr,
                        SizeOfData = size,
                    };
                    return NativeMethods.SetWindowCompositionAttribute(hwnd, ref data) != 0;
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            catch (Exception ex)
            {
                Services.Log.Error("SetWindowCompositionAttribute failed", ex);
                return false;
            }
        }
    }
}

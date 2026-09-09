using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopBuckets.Interop
{
    /// <summary>
    /// Blur-behind for a layered (AllowsTransparency=True) borderless window: the
    /// wallpaper showing through the window's translucent fill gets frosted. Best-effort;
    /// on builds/configs where the OS suppresses it the window is just translucent.
    /// </summary>
    internal static class AcrylicHelper
    {
        public static void Apply(Window window) => Apply(window, blur: true);

        /// <param name="blur">When false the OS blur-behind is switched off, leaving the
        /// window merely translucent (its own fill alpha still shows the wallpaper, just
        /// unfrosted). The Appearance &gt; "Frosted glass" toggle drives this.</param>
        public static void Apply(Window window, bool blur)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (!blur)
            {
                SetAccent(hwnd, NativeMethods.AccentState.ACCENT_DISABLED, 0);
                return;
            }

            if (!SetAccent(hwnd, NativeMethods.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND, 0x01000000))
                SetAccent(hwnd, NativeMethods.AccentState.ACCENT_ENABLE_BLURBEHIND, 0);
        }

        private static bool SetAccent(IntPtr hwnd, NativeMethods.AccentState state, uint gradient)
        {
            try
            {
                var accent = new NativeMethods.AccentPolicy
                {
                    AccentState = state,
                    AccentFlags = 0,
                    GradientColor = gradient,
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

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopBuckets.Interop
{
    /// <summary>Gives a borderless window a frosted-glass (acrylic blur) backdrop with
    /// rounded corners. Tries the Windows 11 DWM system backdrop first, then the legacy
    /// acrylic blur; no-ops if neither is available.</summary>
    internal static class AcrylicHelper
    {
        public static void Apply(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            // Extend the DWM frame across the whole client area so transparent WPF
            // regions show the composited backdrop.
            var margins = new NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

            int round = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            int backdrop = NativeMethods.DWMSBT_TRANSIENTWINDOW; // acrylic
            int hr = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

            if (hr != 0)
                ApplyLegacyAcrylic(hwnd);
        }

        private static void ApplyLegacyAcrylic(IntPtr hwnd)
        {
            try
            {
                var accent = new NativeMethods.AccentPolicy
                {
                    AccentState = NativeMethods.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                    GradientColor = 0x33000000, // AABBGGRR — ~20% black tint
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
                    NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            catch (Exception ex) { Services.Log.Error("Legacy acrylic failed", ex); }
        }
    }
}

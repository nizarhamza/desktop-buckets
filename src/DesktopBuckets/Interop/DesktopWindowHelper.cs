using System;
using System.Windows;
using System.Windows.Interop;

namespace DesktopBuckets.Interop
{
    /// <summary>
    /// Makes a WPF window behave like a desktop widget: never activates, never
    /// steals focus, and stays pinned to the bottom of the Z-order so ordinary
    /// windows always sit on top of it while it remains visible on "Show desktop".
    ///
    /// This is the pragmatic alternative to re-parenting into WorkerW, which is
    /// fragile with third-party wallpaper tools and multi-monitor setups.
    /// </summary>
    internal static class DesktopWindowHelper
    {
        public static void MakeDesktopWidget(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("Window handle not created yet; call from SourceInitialized or later.");

            int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
            ex &= ~NativeMethods.WS_EX_APPWINDOW;
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);

            SendToBottom(hwnd);

            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);
        }

        public static void SendToBottom(IntPtr hwnd)
        {
            NativeMethods.SetWindowPos(
                hwnd, NativeMethods.HWND_BOTTOM,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER |
                NativeMethods.SWP_NOSENDCHANGING);
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_WINDOWPOSCHANGING)
            {
                // Veto any attempt by the shell / other apps to raise us above
                // normal windows. We tolerate Z changes but force insertAfter = BOTTOM.
                var wp = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
                wp.hwndInsertAfter = NativeMethods.HWND_BOTTOM;
                wp.flags |= NativeMethods.SWP_NOACTIVATE;
                System.Runtime.InteropServices.Marshal.StructureToPtr(wp, lParam, false);
            }
            return IntPtr.Zero;
        }
    }
}

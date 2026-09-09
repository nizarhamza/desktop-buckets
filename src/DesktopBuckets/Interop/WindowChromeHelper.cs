using System;
using System.Windows;
using System.Windows.Interop;
using DesktopBuckets.Models;
using DesktopBuckets.Services;

namespace DesktopBuckets.Interop
{
    /// <summary>
    /// Keeps a normal (title-bar) window's non-client area in step with the app theme:
    /// dark caption buttons + text in dark mode, light in light mode, rounded corners
    /// either way. Re-applies itself whenever <see cref="AppearanceService"/> reports a
    /// theme change, and detaches when the window closes.
    /// </summary>
    internal static class WindowChromeHelper
    {
        /// <summary>Hook <paramref name="window"/> so its frame tracks the theme for its
        /// whole lifetime. Call any time before the window is shown.</summary>
        public static void Attach(Window window)
        {
            void ApplyNow() => Apply(window, ThemeManager.Current == AppTheme.Dark);

            if (new WindowInteropHelper(window).Handle != IntPtr.Zero) ApplyNow();
            window.SourceInitialized += (_, _) => ApplyNow();

            void OnThemeChanged() => window.Dispatcher.BeginInvoke(new Action(ApplyNow));
            AppearanceService.Changed += OnThemeChanged;
            window.Closed += (_, _) => AppearanceService.Changed -= OnThemeChanged;
        }

        public static void Apply(Window window, bool dark)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int on = dark ? 1 : 0;
                if (NativeMethods.DwmSetWindowAttribute(
                        hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                {
                    NativeMethods.DwmSetWindowAttribute(
                        hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1, ref on, sizeof(int));
                }

                int corner = NativeMethods.DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(
                    hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

                // Nudge the frame so an already-visible window repaints its caption in
                // the new colour instead of waiting for the next resize/redraw.
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
            }
            catch (Exception ex)
            {
                Log.Error("WindowChromeHelper.Apply failed", ex);
            }
        }
    }
}

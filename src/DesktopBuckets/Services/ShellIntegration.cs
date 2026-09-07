using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace DesktopBuckets.Services
{
    /// <summary>
    /// Registers a "New Bucket" entry on the desktop / folder-background context menu.
    /// HKCU only, so no elevation is ever required. This is a plain <c>shell\verb</c>
    /// command, not an entry inside the "New &#9658;" flyout (that needs a ShellNew
    /// handler, which is awkward for folders).
    /// </summary>
    public static class ShellIntegration
    {
        private const string VerbKey = @"Software\Classes\Directory\Background\shell\DesktopBuckets.NewBucket";
        private const string DesktopVerbKey = @"Software\Classes\DesktopBackground\shell\DesktopBuckets.NewBucket";

        public static bool IsRegistered
        {
            get
            {
                using var k = Registry.CurrentUser.OpenSubKey(VerbKey);
                return k != null;
            }
        }

        public static void Register()
        {
            var exe = ProcessPath();
            WriteVerb(VerbKey, exe);
            WriteVerb(DesktopVerbKey, exe);
            NotifyShell();
        }

        public static void Unregister()
        {
            TryDelete(VerbKey);
            TryDelete(DesktopVerbKey);
            NotifyShell();
        }

        private static void WriteVerb(string keyPath, string exe)
        {
            using var verb = Registry.CurrentUser.CreateSubKey(keyPath, true);
            verb.SetValue(null, "New Bucket");
            verb.SetValue("Icon", $"\"{exe}\",0");
            // %V = the folder the user right-clicked in (empty for the pure desktop background).
            using var cmd = verb.CreateSubKey("command", true);
            cmd.SetValue(null, $"\"{exe}\" --new-bucket \"%V\"");
        }

        private static void TryDelete(string keyPath)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); }
            catch (Exception) { /* best effort */ }
        }

        private static string ProcessPath()
        {
            var p = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(p) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return p;
            return Process.GetCurrentProcess().MainModule?.FileName ?? "DesktopBuckets.exe";
        }

        private static void NotifyShell()
        {
            // SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0)
            try { SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); }
            catch (Exception) { }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    }
}

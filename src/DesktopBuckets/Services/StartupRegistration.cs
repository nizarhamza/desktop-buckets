using System;
using Microsoft.Win32;

namespace DesktopBuckets.Services
{
    /// <summary>The per-user "run at sign-in" Run key. Also written by the installer's
    /// autostart task; this lets the app read and flip it from Settings.</summary>
    internal static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "DesktopBuckets";

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                    return k?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
                }
                catch (Exception) { return false; }
            }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
                if (enabled)
                {
                    var exe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exe))
                        k.SetValue(ValueName, $"\"{exe}\"");
                }
                else
                {
                    k.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
            catch (Exception ex)
            {
                Log.Error("StartupRegistration.SetEnabled failed", ex);
            }
        }
    }
}

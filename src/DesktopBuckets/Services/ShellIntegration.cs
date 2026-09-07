using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace DesktopBuckets.Services
{
    /// <summary>
    /// Adds a "New Bucket" entry to the desktop / folder context menu.
    ///
    /// Preferred path — a signed <b>sparse MSIX package</b> whose
    /// <c>windows.fileExplorerContextMenus</c> extension registers an
    /// <c>IExplorerCommand</c> handler (DesktopBuckets.ShellExt.dll). This is the
    /// only way onto the Windows 11 <b>main</b> context menu. Enabling it needs one
    /// elevation prompt (to trust the bundled dev certificate).
    ///
    /// Fallback path (dev builds without the package) — a plain HKCU
    /// <c>Directory\Background\shell</c> verb, which on Windows 11 lands under
    /// "Show more options".
    /// </summary>
    public static class ShellIntegration
    {
        private const string PackageName = "DesktopBuckets.ShellExt";
        private const string LegacyVerbKey = @"Software\Classes\Directory\Background\shell\DesktopBuckets.NewBucket";
        private const string LegacyDesktopVerbKey = @"Software\Classes\DesktopBackground\shell\DesktopBuckets.NewBucket";

        private static string BaseDir => AppContext.BaseDirectory;
        private static string MsixPath => Path.Combine(BaseDir, "DesktopBuckets.Package.msix");
        private static string CerPath => Path.Combine(BaseDir, "DesktopBuckets.cer");
        private static string ShellExtDll => Path.Combine(BaseDir, "DesktopBuckets.ShellExt.dll");

        /// <summary>True when this build ships the shell-extension package (installed builds).</summary>
        public static bool PackagedModeAvailable =>
            File.Exists(MsixPath) && File.Exists(CerPath) && File.Exists(ShellExtDll);

        private static bool? _cachedRegistered;
        private static DateTime _cachedAt;

        public static bool IsRegistered
        {
            get
            {
                if (PackagedModeAvailable)
                {
                    if (_cachedRegistered is { } c && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(10))
                        return c;
                    bool r = QueryPackageInstalled();
                    _cachedRegistered = r;
                    _cachedAt = DateTime.UtcNow;
                    return r;
                }

                using var k = Registry.CurrentUser.OpenSubKey(LegacyVerbKey);
                return k != null;
            }
        }

        public static void Register()
        {
            if (PackagedModeAvailable)
            {
                RunElevated($"-cer \"{CerPath}\" -msix \"{MsixPath}\" -extloc \"{BaseDir.TrimEnd('\\')}\" -action install");
                _cachedRegistered = null;
                return;
            }
            RegisterLegacy();
        }

        public static void Unregister()
        {
            if (PackagedModeAvailable)
            {
                // Remove-AppxPackage is per-user; no elevation required.
                RunPowerShell(
                    $"Get-AppxPackage -Name {PackageName} | Remove-AppxPackage -ErrorAction SilentlyContinue",
                    elevated: false, wait: true);
                _cachedRegistered = null;
                return;
            }
            UnregisterLegacy();
        }

        // ---- packaged mode helpers ------------------------------------

        private static bool QueryPackageInstalled()
        {
            try
            {
                var psi = new ProcessStartInfo("powershell",
                    $"-NoProfile -ExecutionPolicy Bypass -Command \"if (Get-AppxPackage -Name {PackageName}) {{ exit 0 }} else {{ exit 1 }}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi)!;
                p.WaitForExit(8000);
                return p.HasExited && p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Log.Error("QueryPackageInstalled failed", ex);
                return false;
            }
        }

        /// <summary>Writes the helper script, then runs it elevated. Throws
        /// <see cref="OperationCanceledException"/> if the user declines the UAC prompt.</summary>
        private static void RunElevated(string args)
        {
            var script = Path.Combine(Path.GetTempPath(), "DesktopBuckets.shellext.ps1");
            File.WriteAllText(script, ElevatedScript, new UTF8Encoding(false));

            var psi = new ProcessStartInfo("powershell",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" {args}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            try
            {
                using var p = Process.Start(psi)!;
                p.WaitForExit();
                if (p.ExitCode != 0)
                    Log.Error($"Elevated shell-ext script exited {p.ExitCode}");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException("The elevation prompt was declined.", ex);
            }
        }

        private static void RunPowerShell(string command, bool elevated, bool wait)
        {
            var psi = new ProcessStartInfo("powershell",
                $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"")
            {
                UseShellExecute = elevated,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (elevated) psi.Verb = "runas";

            try
            {
                using var p = Process.Start(psi)!;
                if (wait) p.WaitForExit(30000);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException("The elevation prompt was declined.", ex);
            }
        }

        // Trusts the bundled dev cert (machine store) and registers / removes the
        // sparse package. Args: -action install|uninstall -cer <p> -msix <p> -extloc <dir>
        private const string ElevatedScript = @"
param(
  [string]$action = 'install',
  [string]$cer,
  [string]$msix,
  [string]$extloc
)
$ErrorActionPreference = 'Stop'
try {
  if ($action -eq 'install') {
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Add-AppxPackage -Path $msix -ExternalLocation $extloc -ForceApplicationShutdown
  }
  else {
    Get-AppxPackage -Name 'DesktopBuckets.ShellExt' | Remove-AppxPackage -ErrorAction SilentlyContinue
  }
  # Reload Explorer so the menu updates immediately.
  Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
  exit 0
} catch {
  Write-Error $_
  exit 1
}
";

        // ---- legacy (Show more options) fallback ---------------------

        private static void RegisterLegacy()
        {
            var exe = ProcessPath();
            WriteLegacyVerb(LegacyVerbKey, exe);
            WriteLegacyVerb(LegacyDesktopVerbKey, exe);
            NotifyShell();
        }

        private static void UnregisterLegacy()
        {
            TryDeleteKey(LegacyVerbKey);
            TryDeleteKey(LegacyDesktopVerbKey);
            NotifyShell();
        }

        private static void WriteLegacyVerb(string keyPath, string exe)
        {
            using var verb = Registry.CurrentUser.CreateSubKey(keyPath, true);
            verb.SetValue(null, "New Bucket");
            verb.SetValue("Icon", $"\"{exe}\",0");
            using var cmd = verb.CreateSubKey("command", true);
            cmd.SetValue(null, $"\"{exe}\" --new-bucket \"%V\"");
        }

        private static void TryDeleteKey(string keyPath)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); }
            catch (Exception) { }
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
            try { SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); }
            catch (Exception) { }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    }
}

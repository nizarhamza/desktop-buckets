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
                RunElevated(BuildElevatedScript("install"));
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

        /// <summary>Publisher string in the package manifest; the family name hashes it.</summary>
        private const string PackagePublisher = "CN=DesktopBuckets Dev";

        private static readonly string PackageFamilyName =
            PackageName + "_" + PublisherIdHash(PackagePublisher);

        /// <summary>In-process, instant: asks the AppModel API whether the package family
        /// is registered for this user. Replaces spawning <c>powershell Get-AppxPackage</c>,
        /// which blocked the UI thread for seconds at startup and when opening menus.</summary>
        private static bool QueryPackageInstalled()
        {
            try
            {
                return Interop.NativeMethods.IsPackageFamilyInstalled(PackageFamilyName);
            }
            catch (Exception ex)
            {
                Log.Error("QueryPackageInstalled failed", ex);
                return false;
            }
        }

        /// <summary>The 13-character publisher id Windows appends to a package family
        /// name: first 8 bytes of SHA-256 over the UTF-16LE publisher string, encoded
        /// as 65 bits of Crockford base32.</summary>
        internal static string PublisherIdHash(string publisher)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
            const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
            // 8 bytes = 64 bits, padded with one zero bit to 65 = 13 × 5.
            ulong bits = 0;
            for (int i = 0; i < 8; i++) bits = (bits << 8) | hash[i];
            var sb = new StringBuilder(13);
            for (int i = 0; i < 13; i++)
            {
                // The 65-bit value is (bits << 1); group i is its bits [64-5i .. 60-5i],
                // i.e. (bits << 1) >> (60 - 5i) == bits >> (59 - 5i); the last group
                // needs the padding bit, so it shifts left instead.
                int shift = 59 - i * 5;
                ulong group = shift >= 0
                    ? (bits >> shift) & 0x1F
                    : (bits << -shift) & 0x1F;
                sb.Append(alphabet[(int)group]);
            }
            return sb.ToString();
        }

        /// <summary>How long the elevated helper may run before we stop waiting for it.
        /// It restarts Explorer, so allow a generous window.</summary>
        private static readonly TimeSpan ElevatedTimeout = TimeSpan.FromMinutes(2);

        /// <summary>Runs <paramref name="script"/> elevated. The script travels on the
        /// command line as <c>-EncodedCommand</c> — it never touches disk, so there is no
        /// user-writable file for another process to swap before it runs as admin.
        /// Throws <see cref="OperationCanceledException"/> if the user declines UAC.</summary>
        private static void RunElevated(string script)
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo("powershell",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            try
            {
                using var p = Process.Start(psi)!;
                if (!p.WaitForExit((int)ElevatedTimeout.TotalMilliseconds))
                {
                    Log.Error($"Elevated shell-ext script did not finish within {ElevatedTimeout.TotalSeconds:F0}s; giving up on it.");
                    return;
                }
                if (p.ExitCode != 0)
                    Log.Error($"Elevated shell-ext script exited {p.ExitCode}");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException("The elevation prompt was declined.", ex);
            }
        }

        /// <summary>Single-quoted PowerShell string literal (the only escape is <c>''</c>).</summary>
        private static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

        private static string BuildElevatedScript(string action) =>
            ElevatedScriptTemplate
                .Replace("__ACTION__", PsQuote(action))
                .Replace("__CER__", PsQuote(CerPath))
                .Replace("__MSIX__", PsQuote(MsixPath))
                .Replace("__EXTLOC__", PsQuote(BaseDir.TrimEnd('\\')));

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

        // Trusts the bundled dev cert and registers / removes the sparse package.
        // Placeholders are replaced with single-quoted literals by BuildElevatedScript.
        //
        // Sideloading a signed package needs the cert in TrustedPeople ONLY. Earlier
        // builds also imported it into Root, which made the dev key a trusted
        // certificate authority for the whole machine — so every run removes it from
        // Root again, on install as well as uninstall.
        private const string ElevatedScriptTemplate = @"
$ErrorActionPreference = 'Stop'
$action = __ACTION__
$cer    = __CER__
$msix   = __MSIX__
$extloc = __EXTLOC__
try {
  $thumb = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cer).Thumbprint
  Get-ChildItem Cert:\LocalMachine\Root |
    Where-Object { $_.Thumbprint -eq $thumb } |
    Remove-Item -ErrorAction SilentlyContinue
  if ($action -eq 'install') {
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    Add-AppxPackage -Path $msix -ExternalLocation $extloc -ForceApplicationShutdown
  }
  else {
    Get-AppxPackage -Name 'DesktopBuckets.ShellExt' | Remove-AppxPackage -ErrorAction SilentlyContinue
    Get-ChildItem Cert:\LocalMachine\TrustedPeople |
      Where-Object { $_.Thumbprint -eq $thumb } |
      Remove-Item -ErrorAction SilentlyContinue
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

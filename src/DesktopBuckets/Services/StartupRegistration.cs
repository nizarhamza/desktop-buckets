using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DesktopBuckets.Services
{
    /// <summary>
    /// "Start Desktop Buckets when I sign in", implemented as a per-user <b>logon
    /// scheduled task</b> (<c>\DesktopBuckets-Autostart</c>).
    /// <para>
    /// Older builds used an <c>HKCU\…\CurrentVersion\Run</c> value. Explorer runs those
    /// entries at the tail of a throttled, serialized startup sweep — routinely a minute
    /// or more after the desktop appears — which read as "it never started", and a
    /// second copy launched in the meantime just exited on the single-instance guard
    /// with nothing on screen. A logon task fires at a fixed short delay instead and is
    /// not gated behind that queue. Whenever this class runs it also clears the legacy
    /// Run value (and any stale Task-Manager "disabled" flag beside it).
    /// </para>
    /// Both <see cref="Register"/> and <see cref="Unregister"/> are also reachable from
    /// the installer through the <c>--register-autostart</c> / <c>--unregister-autostart</c>
    /// one-shot verbs, so the Settings toggle and the installer share one definition.
    /// </summary>
    internal static class StartupRegistration
    {
        private const string TaskName = "DesktopBuckets-Autostart";
        private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string LegacyRunApprovedKey =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string LegacyValueName = "DesktopBuckets";

        /// <summary>True when the logon task exists and is not disabled.</summary>
        public static bool IsEnabled
        {
            get
            {
                try
                {
                    var (code, stdout) = RunSchtasks($"/Query /TN \"{TaskName}\" /XML ONE", captureOut: true);
                    if (code != 0) return false; // no such task
                    // A missing <Enabled> defaults to true; only an explicit false — on the
                    // task or its trigger, e.g. switched off in Task Manager > Startup —
                    // counts as disabled.
                    return !Regex.IsMatch(stdout, @"<Enabled>\s*false\s*</Enabled>", RegexOptions.IgnoreCase);
                }
                catch (Exception ex)
                {
                    Log.Error("StartupRegistration.IsEnabled query failed", ex);
                    return false;
                }
            }
        }

        public static void SetEnabled(bool enabled)
        {
            if (enabled) Register();
            else Unregister();
        }

        /// <summary>Create (or replace) the logon task, pointed at the running exe.</summary>
        public static void Register()
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    Log.Error("StartupRegistration.Register: Environment.ProcessPath is empty; skipping.");
                    return;
                }

                var xmlPath = Path.Combine(Path.GetTempPath(), $"db-autostart-{Guid.NewGuid():N}.xml");
                // schtasks /Create /XML expects UTF-16.
                File.WriteAllText(xmlPath, BuildTaskXml(exe, CurrentUserSid()), new UnicodeEncoding(false, true));
                try
                {
                    var (code, _) = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F", captureOut: false);
                    if (code == 0) Log.Info($"StartupRegistration: logon task registered -> \"{exe}\" --autostart");
                    else Log.Error($"StartupRegistration: schtasks /Create exited {code}.");
                }
                finally
                {
                    try { File.Delete(xmlPath); } catch { /* best effort */ }
                }

                ClearLegacyRunValue();
            }
            catch (Exception ex)
            {
                Log.Error("StartupRegistration.Register failed", ex);
            }
        }

        /// <summary>Remove the logon task.</summary>
        public static void Unregister()
        {
            try
            {
                var (code, _) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F", captureOut: false);
                // 0 = deleted, 1 = it wasn't there. Anything else is worth a line.
                if (code is 0 or 1) Log.Info("StartupRegistration: logon task removed.");
                else Log.Error($"StartupRegistration: schtasks /Delete exited {code}.");

                ClearLegacyRunValue();
            }
            catch (Exception ex)
            {
                Log.Error("StartupRegistration.Unregister failed", ex);
            }
        }

        /// <summary>The task definition: fire ~20 s after <paramref name="userSid"/> signs
        /// in, run <paramref name="exePath"/> unelevated with <c>--autostart</c>, with no
        /// battery gating and no run-time limit (it's a resident app).</summary>
        internal static string BuildTaskXml(string exePath, string userSid)
        {
            string cmd = SecurityElement.Escape(exePath) ?? exePath;
            return $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <RegistrationInfo>
                    <Author>Desktop Buckets</Author>
                    <Description>Starts Desktop Buckets when you sign in.</Description>
                    <URI>\{TaskName}</URI>
                  </RegistrationInfo>
                  <Triggers>
                    <LogonTrigger>
                      <Enabled>true</Enabled>
                      <UserId>{userSid}</UserId>
                      <Delay>PT20S</Delay>
                    </LogonTrigger>
                  </Triggers>
                  <Principals>
                    <Principal id="Author">
                      <UserId>{userSid}</UserId>
                      <LogonType>InteractiveToken</LogonType>
                      <RunLevel>LeastPrivilege</RunLevel>
                    </Principal>
                  </Principals>
                  <Settings>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <AllowHardTerminate>true</AllowHardTerminate>
                    <StartWhenAvailable>false</StartWhenAvailable>
                    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                    <IdleSettings>
                      <StopOnIdleEnd>false</StopOnIdleEnd>
                      <RestartOnIdle>false</RestartOnIdle>
                    </IdleSettings>
                    <AllowStartOnDemand>true</AllowStartOnDemand>
                    <Enabled>true</Enabled>
                    <Hidden>false</Hidden>
                    <RunOnlyIfIdle>false</RunOnlyIfIdle>
                    <WakeToRun>false</WakeToRun>
                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                    <Priority>7</Priority>
                  </Settings>
                  <Actions Context="Author">
                    <Exec>
                      <Command>{cmd}</Command>
                      <Arguments>--autostart</Arguments>
                    </Exec>
                  </Actions>
                </Task>
                """;
        }

        private static string CurrentUserSid()
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.User?.Value ?? throw new InvalidOperationException("No user SID on the current token.");
        }

        private static (int code, string stdout) RunSchtasks(string arguments, bool captureOut)
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start schtasks.exe.");
            string outText = p.StandardOutput.ReadToEnd();
            string errText = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(); } catch { /* ignore */ }
                Log.Error($"schtasks {Verb(arguments)} timed out.");
                return (-1, outText);
            }

            // /Query returns 1 for a missing task — that's an expected answer, not an error.
            if (p.ExitCode != 0 && !arguments.StartsWith("/Query", StringComparison.OrdinalIgnoreCase))
                Log.Info($"schtasks {Verb(arguments)} -> {p.ExitCode} :: {Collapse(outText)} {Collapse(errText)}".TrimEnd());

            return (p.ExitCode, captureOut ? outText : string.Empty);
        }

        private static string Verb(string args)
        {
            int sp = args.IndexOf(' ');
            return sp > 0 ? args[..sp] : args;
        }

        private static string Collapse(string s) =>
            string.IsNullOrWhiteSpace(s) ? "" : Regex.Replace(s.Trim(), @"\s+", " ");

        /// <summary>Delete the pre-scheduled-task <c>Run</c> value and any Task-Manager
        /// enable/disable flag paired with it, so the two mechanisms can't both fire.</summary>
        private static void ClearLegacyRunValue()
        {
            foreach (var sub in new[] { LegacyRunKey, LegacyRunApprovedKey })
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(sub, writable: true);
                    if (k?.GetValue(LegacyValueName) is null) continue;
                    k.DeleteValue(LegacyValueName, throwOnMissingValue: false);
                    Log.Info($@"StartupRegistration: removed legacy HKCU\{sub}\{LegacyValueName}.");
                }
                catch (Exception ex)
                {
                    Log.Error($@"StartupRegistration: clearing HKCU\{sub}\{LegacyValueName} failed", ex);
                }
            }
        }
    }
}

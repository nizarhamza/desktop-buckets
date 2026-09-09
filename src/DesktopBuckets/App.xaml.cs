using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using DesktopBuckets.Services;

namespace DesktopBuckets
{
    public partial class App : Application
    {
        private SingleInstance? _single;
        private BucketManager? _manager;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Who am I, and how was I launched? Without this the sign-in path left no
            // trace at all — you couldn't tell whether autostart had even fired, or
            // which exe/instance won the single-instance race.
            Services.Log.Info($"OnStartup pid={Environment.ProcessId} exe=\"{Environment.ProcessPath}\" " +
                              $"args=[{string.Join(' ', e.Args)}]");

            // One-shot commands that don't need a running instance.
            if (e.Args.Any(a => a.Equals("--register-shell", StringComparison.OrdinalIgnoreCase)))
            {
                ShellIntegration.Register();
                Shutdown();
                return;
            }
            if (e.Args.Any(a => a.Equals("--unregister-shell", StringComparison.OrdinalIgnoreCase)))
            {
                ShellIntegration.Unregister();
                Shutdown();
                return;
            }
            if (e.Args.Any(a => a.Equals("--register-autostart", StringComparison.OrdinalIgnoreCase)))
            {
                StartupRegistration.SetEnabled(true);
                Shutdown();
                return;
            }
            if (e.Args.Any(a => a.Equals("--unregister-autostart", StringComparison.OrdinalIgnoreCase)))
            {
                StartupRegistration.SetEnabled(false);
                Shutdown();
                return;
            }
            bool isDumpGrid = e.Args.Any(a => a.Equals("--dump-grid", StringComparison.OrdinalIgnoreCase));
            bool isRealignNow = e.Args.Any(a => a.Equals("--realign-now", StringComparison.OrdinalIgnoreCase));
            if (isDumpGrid || isRealignNow)
            {
                // Both of these touch the SAME live desktop icons a running instance
                // might be actively pushing aside mid-drag. Two separate, uncoordinated
                // processes writing icon positions at the same time is a real race —
                // confirmed live: running this standalone while the app was mid-drag
                // produced real icons landing on top of each other that neither
                // process's own placement logic would ever produce on its own, since
                // each has zero visibility into what the OTHER is doing to the same
                // listview at the same moment. If an instance is already running, hand
                // the request to IT over the existing IPC pipe (same mechanism
                // --new-bucket/--settings/--quit already use) instead of touching icons
                // from here; only run standalone when nothing is running to hand it to —
                // the documented "recovery without a running instance" case.
                using var probe = new SingleInstance();
                if (!probe.IsPrimary)
                {
                    SingleInstance.ForwardToPrimary(e.Args);
                    Shutdown();
                    return;
                }

                if (isDumpGrid)
                {
                    try
                    {
                        var text = Interop.DesktopShell.DescribeGrid() + Interop.DesktopShell.DescribeTileWindows();
                        var path = System.IO.Path.Combine(BucketStore.AppDataDir, "grid-dump.txt");
                        System.IO.File.WriteAllText(path, text);
                        Services.Log.Info($"--dump-grid written to {path}");
                    }
                    catch (Exception ex) { Services.Log.Error("--dump-grid failed", ex); }
                }
                else
                {
                    try
                    {
                        Interop.DesktopShell.EnsureSnapToGridDisabled();
                        int moved = Interop.DesktopShell.RealignAllIconsToGrid();
                        // RealignAllIconsToGrid only SCHEDULES icon moves — IconAnimator
                        // slides them over ~260ms via a DispatcherTimer that needs the
                        // message pump running. This process has no window and is about
                        // to Shutdown() right after this block, which kills that pump
                        // immediately: without this call, most icons (everything past
                        // the first ~16ms tick) would never actually reach their
                        // resolved position, even though the "moved N icon(s)" log line
                        // below claims they did (it counts scheduling, not completion).
                        // FlushAndRelease writes every in-flight tween straight to its
                        // final target instead of animating it, which is exactly right
                        // for a one-shot CLI/recovery path with no UI to animate for.
                        Interop.IconAnimator.FlushAndRelease();
                        Services.Log.Info($"--realign-now moved {moved} icon(s).");
                    }
                    catch (Exception ex) { Services.Log.Error("--realign-now failed", ex); }
                }
                Shutdown();
                return;
            }

            _single = new SingleInstance();
            if (!_single.IsPrimary)
            {
                // Hand off to the instance that already owns this session. Forward even
                // with no args — a bare autostart relaunch, or a double-click while the
                // app is running — as "--autostart" so the primary re-shows its tiles
                // instead of this process just vanishing with nothing on screen.
                Services.Log.Info("Another instance owns this session; forwarding and exiting.");
                SingleInstance.ForwardToPrimary(e.Args.Length > 0 ? e.Args : new[] { "--autostart" });
                _single.Dispose();
                _single = null;
                Shutdown();
                return;
            }

            // `--quit` with no instance to quit: nothing to do.
            if (e.Args.Any(a => a.Equals("--quit", StringComparison.OrdinalIgnoreCase)))
            {
                _single.Dispose();
                _single = null;
                Shutdown();
                return;
            }

            DispatcherUnhandledException += OnUnhandledException;

            // Load look & feel and apply the light/dark palette before any window shows.
            Services.AppearanceService.Initialize(Dispatcher);
            Services.ThemeManager.Initialize();

            // Subscribe (inside Start) before listening, so a command that arrives in
            // the first milliseconds isn't dropped on the floor.
            _manager = new BucketManager(_single);
            try
            {
                _manager.Start();
            }
            catch (Exception ex)
            {
                // A throw here used to kill the process before the tray or IPC server
                // existed, with nothing logged on the sign-in path. Stay up (tray/IPC
                // may still have come up) and leave a trace instead.
                Services.Log.Error("BucketManager.Start faulted during startup", ex);
            }
            _single.StartServer();

            var i = Array.FindIndex(e.Args, a => a.Equals("--new-bucket", StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                var parent = i + 1 < e.Args.Length ? e.Args[i + 1].Trim().Trim('"') : null;
                if (parent is not null && (parent.Length == 0 || parent == "%V")) parent = null;
                Dispatcher.BeginInvoke(() => _manager.PromptCreateBucket(parent));
            }

            if (e.Args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
                Dispatcher.BeginInvoke(() => _manager.OpenSettings());
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Services.Log.Error("Unhandled dispatcher exception", e.Exception);
            e.Handled = true; // a broken tile shouldn't take the whole app down
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _manager?.Dispose();
            _single?.Dispose();
            base.OnExit(e);
        }
    }
}

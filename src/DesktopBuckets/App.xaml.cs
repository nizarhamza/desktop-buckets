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
            // Diagnostic: what the snapping code sees (grid, icons, tile rects) -> grid-dump.txt
            if (e.Args.Any(a => a.Equals("--dump-grid", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var text = Interop.DesktopShell.DescribeGrid() + Interop.DesktopShell.DescribeTileWindows();
                    var path = System.IO.Path.Combine(BucketStore.AppDataDir, "grid-dump.txt");
                    System.IO.File.WriteAllText(path, text);
                    Services.Log.Info($"--dump-grid written to {path}");
                }
                catch (Exception ex) { Services.Log.Error("--dump-grid failed", ex); }
                Shutdown();
                return;
            }
            // Same job as the tray's / Settings' "Align icons to grid", runnable without
            // a running instance — handy for a scheduled task or scripted recovery.
            if (e.Args.Any(a => a.Equals("--realign-now", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    Interop.DesktopShell.EnsureSnapToGridDisabled();
                    int moved = Interop.DesktopShell.RealignAllIconsToGrid();
                    Services.Log.Info($"--realign-now moved {moved} icon(s).");
                }
                catch (Exception ex) { Services.Log.Error("--realign-now failed", ex); }
                Shutdown();
                return;
            }

            _single = new SingleInstance();
            if (!_single.IsPrimary)
            {
                if (e.Args.Length > 0)
                    SingleInstance.ForwardToPrimary(e.Args);
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

            // Subscribe (inside Start) before listening, so a command that arrives in
            // the first milliseconds isn't dropped on the floor.
            _manager = new BucketManager(_single);
            _manager.Start();
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

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

            DispatcherUnhandledException += OnUnhandledException;

            _single.StartServer();
            _manager = new BucketManager(_single);
            _manager.Start();

            var i = Array.FindIndex(e.Args, a => a.Equals("--new-bucket", StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                var parent = i + 1 < e.Args.Length ? e.Args[i + 1].Trim().Trim('"') : null;
                if (parent is not null && (parent.Length == 0 || parent == "%V")) parent = null;
                Dispatcher.BeginInvoke(() => _manager.PromptCreateBucket(parent));
            }
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

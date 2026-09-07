using System;
using System.IO;
using System.Windows.Threading;

namespace DesktopBuckets.Services
{
    /// <summary>
    /// Watches a bucket folder (non-recursive) and raises <see cref="Changed"/> on the
    /// UI thread, debounced, whenever files appear / disappear / are renamed / rewritten.
    /// </summary>
    public sealed class BucketWatcher : IDisposable
    {
        private readonly FileSystemWatcher _fsw;
        private readonly DispatcherTimer _debounce;
        private bool _disposed;

        public event Action? Changed;

        public BucketWatcher(string folderPath, Dispatcher dispatcher, TimeSpan? debounce = null)
        {
            _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = debounce ?? TimeSpan.FromMilliseconds(250),
            };
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                if (!_disposed) Changed?.Invoke();
            };

            _fsw = new FileSystemWatcher(folderPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite |
                               NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            _fsw.Created += OnAny;
            _fsw.Deleted += OnAny;
            _fsw.Renamed += OnAny;
            _fsw.Changed += OnAny;
            _fsw.Error   += (_, _) => Kick(); // buffer overflow etc. -> just rescan

            try { _fsw.EnableRaisingEvents = true; }
            catch (FileNotFoundException) { }
            catch (ArgumentException) { }
        }

        private void OnAny(object sender, FileSystemEventArgs e)
        {
            var name = e.Name ?? string.Empty;
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return;
            if (name.Equals(".bucket.json", StringComparison.OrdinalIgnoreCase)) return;
            Kick();
        }

        private void Kick()
        {
            if (_disposed) return;
            _debounce.Dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                _debounce.Stop();
                _debounce.Start();
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _debounce.Stop();
            _fsw.EnableRaisingEvents = false;
            _fsw.Dispose();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using DesktopBuckets.Models;
using DesktopBuckets.ViewModels;
using DesktopBuckets.Views;

namespace DesktopBuckets.Services
{
    /// <summary>
    /// Owns the running set of buckets: one <see cref="BucketTileWindow"/> and one
    /// <see cref="BucketWatcher"/> per bucket folder, plus the tray icon. Implements
    /// <see cref="IBucketHost"/> so tiles can drive lifecycle actions.
    /// </summary>
    public sealed class BucketManager : IBucketHost, IDisposable
    {
        private sealed class Entry
        {
            public required Bucket Bucket;
            public required BucketTileWindow Window;
            public required BucketWatcher Watcher;
        }

        private readonly BucketStore _store = new();
        private readonly SingleInstance _single;
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private TrayIconController? _tray;
        private int _cascade;
        private bool _disposed;

        public BucketManager(SingleInstance single)
        {
            _single = single;
        }

        public void Start()
        {
            Log.Info("Starting.");
            _store.Load();
            Log.Info($"Index lists {_store.Folders.Count} bucket folder(s).");

            _tray = new TrayIconController(ShellIntegration.IsRegistered);
            _tray.NewBucketRequested += () => PromptCreateBucket();
            _tray.ShowAllRequested += ShowAll;
            _tray.ToggleShellRequested += ToggleShellIntegration;
            _tray.OpenFolderRequested += () => OpenPath(BucketStore.DefaultBucketRoot);
            _tray.QuitRequested += QuitApp;

            _single.CommandReceived += OnForwardedCommand;

            foreach (var folder in _store.Folders.ToList())
                TryLoadBucket(folder);

            if (_entries.Count == 0)
                _tray.ShowBalloon("Desktop Buckets",
                    "Running in the tray. Right-click the tray icon → New bucket to get started.");
        }

        // ---- IBucketHost ------------------------------------------------

        public bool ShellIntegrationEnabled => ShellIntegration.IsRegistered;

        public void PromptCreateBucket(string? parentFolder = null)
        {
            var name = InputDialog.Ask("New bucket", "Bucket name", "New Bucket", "Create");
            if (string.IsNullOrWhiteSpace(name)) return;

            string folder;
            if (!string.IsNullOrWhiteSpace(parentFolder) && Directory.Exists(parentFolder))
            {
                var safe = Bucket.SanitizeName(name!);
                folder = Path.Combine(parentFolder!, safe);
                int n = 2;
                while (Directory.Exists(folder) || File.Exists(folder))
                    folder = Path.Combine(parentFolder!, $"{safe} ({n++})");
            }
            else
            {
                folder = _store.NewBucketFolderPath(name!);
            }

            try
            {
                Directory.CreateDirectory(folder);
                var bucket = Bucket.LoadOrCreate(folder);
                bucket.Rename(name!); // sets display name; folder already correct
                _store.Add(folder);
                LoadBucket(bucket);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not create the bucket:\n{ex.Message}",
                    "Desktop Buckets", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public void RenameBucket(Bucket bucket, string newName)
        {
            if (!_entries.TryGetValue(bucket.FolderPath, out var entry)) return;

            var oldFolder = bucket.FolderPath;
            bucket.Rename(newName);

            if (!string.Equals(bucket.FolderPath, oldFolder, StringComparison.OrdinalIgnoreCase))
            {
                _store.Replace(oldFolder, bucket.FolderPath);
                entry.Watcher.Dispose();
                _entries.Remove(oldFolder);

                entry = new Entry
                {
                    Bucket = bucket,
                    Window = entry.Window,
                    Watcher = CreateWatcher(bucket),
                };
                _entries[bucket.FolderPath] = entry;
            }

            entry.Window.RefreshFromDisk();
        }

        public void DeleteBucket(Bucket bucket)
        {
            if (!_entries.TryGetValue(bucket.FolderPath, out var entry)) return;

            var result = MessageBox.Show(
                $"Delete the bucket “{bucket.Name}”?\n\n" +
                $"The folder and everything in it will be moved to the Recycle Bin:\n{bucket.FolderPath}",
                "Delete bucket", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            entry.Watcher.Dispose();
            entry.Window.Teardown();
            _entries.Remove(bucket.FolderPath);
            _store.Remove(bucket.FolderPath);

            if (Directory.Exists(bucket.FolderPath) && !RecycleBin.Send(bucket.FolderPath))
                MessageBox.Show("The bucket was removed from the desktop, but its folder could not be sent to the Recycle Bin.",
                    "Desktop Buckets", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public void ToggleShellIntegration(bool enabled)
        {
            try
            {
                if (enabled) ShellIntegration.Register();
                else ShellIntegration.Unregister();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not update the desktop right-click menu:\n{ex.Message}",
                    "Desktop Buckets", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            _tray?.SetShellChecked(ShellIntegration.IsRegistered);
        }

        // ---- bucket wiring -------------------------------------------

        private void TryLoadBucket(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) { _store.Remove(folder); return; }
                var bucket = Bucket.LoadOrCreate(folder);
                bucket.PrunePinned();
                LoadBucket(bucket);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to load bucket {folder}", ex);
            }
        }

        private void LoadBucket(Bucket bucket)
        {
            if (_entries.ContainsKey(bucket.FolderPath)) return;

            var vm = new BucketTileViewModel(bucket);
            var window = new BucketTileWindow(vm, this, _cascade++);
            var watcher = CreateWatcher(bucket);

            _entries[bucket.FolderPath] = new Entry
            {
                Bucket = bucket,
                Window = window,
                Watcher = watcher,
            };

            window.Show();
        }

        private BucketWatcher CreateWatcher(Bucket bucket)
        {
            var watcher = new BucketWatcher(bucket.FolderPath, Application.Current.Dispatcher);
            watcher.Changed += () =>
            {
                if (_disposed) return;
                if (!Directory.Exists(bucket.FolderPath))
                {
                    RemoveVanishedBucket(bucket.FolderPath);
                    return;
                }
                if (_entries.TryGetValue(bucket.FolderPath, out var e))
                    e.Window.RefreshFromDisk();
            };
            return watcher;
        }

        private void RemoveVanishedBucket(string folder)
        {
            if (!_entries.TryGetValue(folder, out var entry)) return;
            entry.Watcher.Dispose();
            entry.Window.Teardown();
            _entries.Remove(folder);
            _store.Remove(folder);
        }

        // ---- misc --------------------------------------------------

        private void OnForwardedCommand(string[] args)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var i = Array.FindIndex(args, a => a.Equals("--new-bucket", StringComparison.OrdinalIgnoreCase));
                if (i >= 0)
                {
                    var parent = i + 1 < args.Length ? args[i + 1].Trim().Trim('"') : null;
                    if (parent is not null && (parent.Length == 0 || parent == "%V")) parent = null;
                    PromptCreateBucket(parent);
                }
            });
        }

        public void ShowAll()
        {
            _cascade = 0;
            foreach (var e in _entries.Values)
            {
                if (!e.Window.IsVisible) e.Window.Show();
                var cfg = e.Bucket.Config;
                if (!cfg.HasStoredPosition)
                {
                    e.Window.Left = SystemParameters.WorkArea.Left + 40 + (_cascade % 8) * 28;
                    e.Window.Top = SystemParameters.WorkArea.Top + 40 + (_cascade % 8) * 28;
                    _cascade++;
                }
            }
        }

        private static void OpenPath(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { Debug.WriteLine(ex.Message); }
        }

        private void QuitApp()
        {
            Dispose();
            Application.Current?.Shutdown();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var e in _entries.Values.ToList())
            {
                e.Watcher.Dispose();
                e.Window.Teardown();
            }
            _entries.Clear();
            _tray?.Dispose();
            _tray = null;
        }
    }
}

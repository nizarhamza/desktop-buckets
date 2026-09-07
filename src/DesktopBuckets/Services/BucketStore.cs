using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DesktopBuckets.Services
{
    /// <summary>
    /// Tracks which folders are buckets. The list itself lives in
    /// <c>%APPDATA%\DesktopBuckets\buckets.json</c>; all other bucket state lives in
    /// each folder's own <c>.bucket.json</c> so a bucket is portable.
    /// </summary>
    public sealed class BucketStore
    {
        private sealed class Index
        {
            public List<string> Folders { get; set; } = new();
        }

        public static string DefaultBucketRoot { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop Buckets");

        // App state lives in a hidden folder under the bucket root, NOT under
        // %APPDATA%/%LOCALAPPDATA%: if the exe ever runs with MSIX package identity
        // (it is declared as the shell package's Application), everything under
        // AppData\{Roaming,Local} is path-redirected to a per-package folder, which
        // silently splits state. %USERPROFILE%\Desktop Buckets never is.
        public static string AppDataDir { get; } = ResolveAppDataDir();

        private static string ResolveAppDataDir()
        {
            var dir = Path.Combine(DefaultBucketRoot, ".app");
            try
            {
                Directory.CreateDirectory(dir);
                var di = new DirectoryInfo(dir);
                if (!di.Attributes.HasFlag(FileAttributes.Hidden))
                    di.Attributes |= FileAttributes.Hidden;

                // One-time migration from the old %APPDATA%\DesktopBuckets location.
                var old = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopBuckets");
                if (Directory.Exists(old))
                    foreach (var name in new[] { "buckets.json", "update.json", "update-state.json" })
                    {
                        var src = Path.Combine(old, name);
                        var dst = Path.Combine(dir, name);
                        if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
                    }
            }
            catch { /* fall through with whatever path we have */ }
            return dir;
        }

        private static string IndexPath => Path.Combine(AppDataDir, "buckets.json");

        private readonly List<string> _folders = new();

        public IReadOnlyList<string> Folders => _folders;

        public void Load()
        {
            Directory.CreateDirectory(AppDataDir);
            Directory.CreateDirectory(DefaultBucketRoot);

            _folders.Clear();
            var idx = JsonUtil.Read<Index>(IndexPath) ?? new Index();
            Log.Info($"BucketStore: index={IndexPath} exists={File.Exists(IndexPath)} rawFolders={idx.Folders.Count}");
            foreach (var raw in idx.Folders)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var f = Models.Bucket.Normalize(raw);
                if (_folders.Any(x => x.Equals(f, StringComparison.OrdinalIgnoreCase))) continue;
                _folders.Add(f);
            }
            int beforePrune = _folders.Count;
            Prune();
            if (_folders.Count != beforePrune)
                Log.Info($"BucketStore: Prune removed {beforePrune - _folders.Count} (folder missing).");
        }

        /// <summary>Drops entries whose folder no longer exists, then persists.</summary>
        public void Prune()
        {
            int before = _folders.Count;
            _folders.RemoveAll(f => !Directory.Exists(f));
            if (_folders.Count != before) Save();
        }

        public void Add(string folderPath)
        {
            folderPath = Models.Bucket.Normalize(folderPath);
            if (!_folders.Any(x => x.Equals(folderPath, StringComparison.OrdinalIgnoreCase)))
            {
                _folders.Add(folderPath);
                Save();
            }
        }

        public void Remove(string folderPath)
        {
            int n = _folders.RemoveAll(x => x.Equals(folderPath, StringComparison.OrdinalIgnoreCase));
            if (n > 0) Save();
        }

        public void Replace(string oldPath, string newPath)
        {
            for (int i = 0; i < _folders.Count; i++)
            {
                if (_folders[i].Equals(oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    _folders[i] = newPath;
                    Save();
                    return;
                }
            }
        }

        public void Save() => JsonUtil.Write(IndexPath, new Index { Folders = _folders.ToList() });

        /// <summary>Picks a fresh, non-colliding folder path under the default root.</summary>
        public string NewBucketFolderPath(string desiredName)
        {
            var name = Models.Bucket.SanitizeName(desiredName);
            if (string.IsNullOrWhiteSpace(name)) name = "Bucket";

            var path = Path.Combine(DefaultBucketRoot, name);
            int n = 2;
            while (Directory.Exists(path) || File.Exists(path))
                path = Path.Combine(DefaultBucketRoot, $"{name} ({n++})");
            return path;
        }
    }
}

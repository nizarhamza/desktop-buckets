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

        public static string AppDataDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopBuckets");

        public static string DefaultBucketRoot { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop Buckets");

        private static string IndexPath => Path.Combine(AppDataDir, "buckets.json");

        private readonly List<string> _folders = new();

        public IReadOnlyList<string> Folders => _folders;

        public void Load()
        {
            Directory.CreateDirectory(AppDataDir);
            Directory.CreateDirectory(DefaultBucketRoot);

            _folders.Clear();
            var idx = JsonUtil.Read<Index>(IndexPath) ?? new Index();
            foreach (var raw in idx.Folders)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var f = Models.Bucket.Normalize(raw);
                if (_folders.Any(x => x.Equals(f, StringComparison.OrdinalIgnoreCase))) continue;
                _folders.Add(f);
            }
            Prune();
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

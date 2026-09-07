using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DesktopBuckets.Services;

namespace DesktopBuckets.Models
{
    /// <summary>
    /// A live bucket: its backing folder plus the <see cref="BucketConfig"/> stored inside it.
    /// Owns config persistence and file enumeration; ranking lives in
    /// <see cref="FileRankingService"/> so it can be tested in isolation.
    /// </summary>
    public sealed class Bucket
    {
        public const string ConfigFileName = ".bucket.json";

        public string FolderPath { get; private set; }
        public BucketConfig Config { get; }

        public string Id => Config.Id;
        public string Name => Config.Name;
        public string ConfigPath => Path.Combine(FolderPath, ConfigFileName);

        public Bucket(string folderPath, BucketConfig config)
        {
            FolderPath = Normalize(folderPath);
            Config = config;
        }

        /// <summary>Canonical full path with the platform separator, no trailing slash.
        /// Shell APIs (SHGetFileInfo, Explorer) reject mixed '/' and '\' separators.</summary>
        public static string Normalize(string path)
        {
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
            catch { return path; }
        }

        public static Bucket LoadOrCreate(string folderPath)
        {
            folderPath = Normalize(folderPath);
            Directory.CreateDirectory(folderPath);
            var configPath = Path.Combine(folderPath, ConfigFileName);
            BucketConfig config = File.Exists(configPath)
                ? JsonUtil.Read<BucketConfig>(configPath) ?? new BucketConfig()
                : new BucketConfig();

            var folderName = new DirectoryInfo(folderPath).Name;
            if (string.IsNullOrWhiteSpace(config.Name) || !File.Exists(configPath))
                config.Name = folderName;

            var bucket = new Bucket(folderPath, config);
            bucket.SaveConfig();
            return bucket;
        }

        public void SaveConfig()
        {
            JsonUtil.WriteHidden(ConfigPath, Config);
        }

        /// <summary>All files sitting directly in the bucket folder (non-recursive),
        /// excluding the config file and OS junk. Never throws for a missing folder.</summary>
        public IReadOnlyList<BucketFile> EnumerateFiles()
        {
            if (!Directory.Exists(FolderPath))
                return Array.Empty<BucketFile>();

            var result = new List<BucketFile>();
            IEnumerable<string> paths;
            try
            {
                paths = Directory.EnumerateFiles(FolderPath, "*", SearchOption.TopDirectoryOnly);
            }
            catch (IOException)
            {
                return Array.Empty<BucketFile>();
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<BucketFile>();
            }

            foreach (var path in paths)
            {
                var name = Path.GetFileName(path);
                if (name.Equals(ConfigFileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) continue;

                DateTime lastWrite;
                try { lastWrite = File.GetLastWriteTimeUtc(path); }
                catch { lastWrite = DateTime.MinValue; }

                var rel = name; // files are direct children, so relative path == name
                Config.LastOpenedUtc.TryGetValue(rel, out var opened);

                result.Add(new BucketFile
                {
                    FullPath = path,
                    RelativePath = rel,
                    Name = name,
                    LastWriteUtc = lastWrite,
                    LastOpenedViaTileUtc = opened,
                    IsPinned = Config.Pinned.Contains(rel, StringComparer.OrdinalIgnoreCase),
                });
            }
            return result;
        }

        public bool IsPinned(string relativePath) =>
            Config.Pinned.Contains(relativePath, StringComparer.OrdinalIgnoreCase);

        public void Pin(string relativePath)
        {
            if (!IsPinned(relativePath))
            {
                Config.Pinned.Add(relativePath); // appended => oldest pin shown first
                SaveConfig();
            }
        }

        public void Unpin(string relativePath)
        {
            int removed = Config.Pinned.RemoveAll(p => p.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) SaveConfig();
        }

        public void RecordOpened(string relativePath)
        {
            Config.LastOpenedUtc[relativePath] = DateTime.UtcNow;
            SaveConfig();
        }

        public void SetSlotCount(int count)
        {
            count = Math.Clamp(count, 1, 9);
            if (Config.SlotCount != count)
            {
                Config.SlotCount = count;
                SaveConfig();
            }
        }

        public void SetPosition(double x, double y)
        {
            Config.X = x;
            Config.Y = y;
            SaveConfig();
        }

        /// <summary>Drops pinned entries whose file is gone. Called on startup only,
        /// so a transiently offline path gets one chance to come back per session.</summary>
        public void PrunePinned()
        {
            Config.Pinned.RemoveAll(rel => !File.Exists(Path.Combine(FolderPath, rel)));

            var liveKeys = new HashSet<string>(
                EnumerateFiles().Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase);
            foreach (var stale in Config.LastOpenedUtc.Keys.Where(k => !liveKeys.Contains(k)).ToList())
                Config.LastOpenedUtc.Remove(stale);

            SaveConfig();
        }

        /// <summary>Renames the bucket, moving the backing folder unless locked or the
        /// target already exists. Returns the effective name.</summary>
        public string Rename(string newName)
        {
            newName = SanitizeName(newName);
            if (string.IsNullOrWhiteSpace(newName)) return Name;

            if (!Config.Locked)
            {
                var parent = Path.GetDirectoryName(FolderPath.TrimEnd(Path.DirectorySeparatorChar));
                if (parent != null)
                {
                    var target = Path.Combine(parent, newName);
                    if (!string.Equals(target, FolderPath, StringComparison.OrdinalIgnoreCase)
                        && !Directory.Exists(target) && !File.Exists(target))
                    {
                        try
                        {
                            Directory.Move(FolderPath, target);
                            FolderPath = Normalize(target);
                        }
                        catch (IOException) { /* fall through: keep folder, change label only */ }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }

            Config.Name = newName;
            SaveConfig();
            return newName;
        }

        public static string SanitizeName(string name)
        {
            if (name == null) return string.Empty;
            name = name.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c.ToString(), string.Empty);
            return name.Trim().TrimEnd('.');
        }
    }
}

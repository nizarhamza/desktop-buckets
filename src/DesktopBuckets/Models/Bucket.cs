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
            bool isFreshConfig = !File.Exists(configPath);
            BucketConfig config = isFreshConfig
                ? new BucketConfig()
                : JsonUtil.Read<BucketConfig>(configPath) ?? new BucketConfig();

            var folderName = new DirectoryInfo(folderPath).Name;
            if (string.IsNullOrWhiteSpace(config.Name) || isFreshConfig)
                config.Name = folderName;

            var bucket = new Bucket(folderPath, config);

            // A brand-new bucket defaults to 4 slots (2x2) — fine for one created empty
            // via "New bucket…", since an empty tile shows its own placeholder text
            // instead of a grid. But turning an EXISTING folder that already has files
            // into a bucket left the grid reserving the full default layout regardless
            // of how many were actually there — e.g. 2 files rendering in a 2x2 grid
            // with two visibly empty slots, extra dead space nobody asked for. Size the
            // initial slot count to the smallest of the 5 allowed shapes (TileShape)
            // that fits what's really in the folder instead; the user can always change
            // it afterwards via the tile's own "Icon slots" menu.
            if (isFreshConfig)
            {
                int fileCount = bucket.EnumerateFiles().Count;
                if (fileCount > 0) config.SlotCount = TileShape.CapacityFor(fileCount);
            }

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
            try
            {
                // FileInfo objects from the directory scan already carry attributes and
                // timestamps — one syscall per directory instead of three per file.
                foreach (var fi in new DirectoryInfo(FolderPath).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                {
                    var name = fi.Name;
                    if (name.StartsWith('.')) continue;                                   // .bucket.json, dotfiles
                    if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) continue;

                    FileAttributes attr;
                    DateTime lastWrite;
                    try
                    {
                        attr = fi.Attributes;
                        lastWrite = fi.LastWriteTimeUtc;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        attr = FileAttributes.Normal;
                        lastWrite = DateTime.MinValue;
                    }
                    if (attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System)) continue;

                    var rel = name; // files are direct children, so relative path == name
                    Config.LastOpenedUtc.TryGetValue(rel, out var opened);

                    result.Add(new BucketFile
                    {
                        FullPath = fi.FullName,
                        RelativePath = rel,
                        Name = name,
                        LastWriteUtc = lastWrite,
                        LastOpenedViaTileUtc = opened,
                        IsPinned = Config.Pinned.Contains(rel, StringComparer.OrdinalIgnoreCase),
                    });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<BucketFile>();
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

        /// <summary>Sets the bucket's capacity, snapped to the nearest of the 5 allowed
        /// shapes' capacities (2, 3, 4, 6, 9 — see TileShape) rather than clamped to an
        /// arbitrary range: any input, including one carried over from before this
        /// shape set existed, always lands on a value that names an exact shape.</summary>
        public void SetSlotCount(int count)
        {
            count = TileShape.CapacityFor(count);
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
            name = name.Trim().TrimEnd('.');

            // CON, PRN, AUX, NUL, COM1-9, LPT1-9 are reserved device names on Windows
            // (with or without an extension); a folder by that name can't be created.
            var stem = name.Split('.', 2)[0];
            if (ReservedDeviceNames.Contains(stem))
                name = "_" + name;
            return name;
        }

        private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };
    }
}

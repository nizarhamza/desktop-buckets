using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopBuckets.Services
{
    /// <summary>Small helpers for atomic (write-temp-then-rename), human-readable JSON
    /// persistence. Reads and writes never throw for ordinary I/O trouble.</summary>
    internal static class JsonUtil
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public static T? Read<T>(string path)
        {
            try
            {
                if (!File.Exists(path)) return default;
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return default;
                return JsonSerializer.Deserialize<T>(json, Options);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return default;
            }
        }

        /// <summary>Atomically replaces <paramref name="path"/> with the serialised value:
        /// the JSON is written to a sibling temp file which is then renamed over the
        /// target, so an interruption leaves either the old file or the new one, never a
        /// truncated half. I/O failures are logged and reported as <c>false</c> rather
        /// than thrown into whatever UI handler triggered the save.</summary>
        public static bool Write<T>(string path, T value)
        {
            var tmp = path + ".tmp";
            bool wasHidden = false;
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(value, Options);
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var w = new StreamWriter(fs))
                {
                    w.Write(json);
                    w.Flush();
                    fs.Flush(flushToDisk: true);
                }

                // A Hidden or ReadOnly target makes the rename fail; clear them for the
                // swap and put Hidden back afterwards.
                if (File.Exists(path))
                {
                    var existing = File.GetAttributes(path);
                    wasHidden = existing.HasFlag(FileAttributes.Hidden);
                    File.SetAttributes(path, existing & ~FileAttributes.Hidden & ~FileAttributes.ReadOnly);
                }

                File.Move(tmp, path, overwrite: true);

                if (wasHidden) SetHidden(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Log.Error($"Could not save {path}", ex);
                try { File.Delete(tmp); } catch { /* best effort */ }
                if (wasHidden) SetHidden(path); // the old file survived; keep it out of sight
                return false;
            }
        }

        private static void SetHidden(string path)
        {
            try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Writes JSON and marks the file Hidden so it stays out of the user's way
        /// when they open the bucket folder in Explorer.</summary>
        public static bool WriteHidden<T>(string path, T value)
        {
            if (!Write(path, value)) return false;
            SetHidden(path);
            return true;
        }
    }
}

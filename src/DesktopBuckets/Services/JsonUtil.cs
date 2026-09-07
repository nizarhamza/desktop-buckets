using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopBuckets.Services
{
    /// <summary>Small helpers for atomic, human-readable JSON persistence.</summary>
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

        public static void Write<T>(string path, T value)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(value, Options);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);

            // Preserve the hidden attribute across the replace.
            FileAttributes? existing = File.Exists(path) ? File.GetAttributes(path) : null;
            if (existing.HasValue)
                File.SetAttributes(path, existing.Value & ~FileAttributes.Hidden & ~FileAttributes.ReadOnly);

            File.Copy(tmp, path, overwrite: true);
            File.Delete(tmp);

            if (existing.HasValue && existing.Value.HasFlag(FileAttributes.Hidden))
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }

        /// <summary>Writes JSON and marks the file Hidden so it stays out of the user's way
        /// when they open the bucket folder in Explorer.</summary>
        public static void WriteHidden<T>(string path, T value)
        {
            Write(path, value);
            try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

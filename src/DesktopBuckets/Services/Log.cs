using System;
using System.IO;

namespace DesktopBuckets.Services
{
    /// <summary>Best-effort rolling log at <c>%USERPROFILE%\Desktop Buckets\.app\log.txt</c>
    /// (<see cref="BucketStore.AppDataDir"/>). Never throws.</summary>
    internal static class Log
    {
        private static readonly object Gate = new();
        private static string LogPath => Path.Combine(BucketStore.AppDataDir, "log.txt");

        public static void Info(string message) => Write("INFO", message);

        public static void Error(string message, Exception? ex = null) =>
            Write("ERROR", ex == null ? message : $"{message} :: {ex}");

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(BucketStore.AppDataDir);
                    var path = LogPath;
                    if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                        File.WriteAllText(path, string.Empty);
                    File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // logging must never break the app
            }
        }
    }
}

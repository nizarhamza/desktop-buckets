using System;
using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopBuckets.Interop;

namespace DesktopBuckets.Services
{
    /// <summary>
    /// Resolves a file path to a WPF <see cref="ImageSource"/> via the Windows shell
    /// (<c>SHGetFileInfo</c>). Results are frozen and cached. Extension-based caching
    /// for ordinary documents; per-path for types that carry their own icon
    /// (.exe/.lnk/.ico/images).
    /// </summary>
    public sealed class IconService
    {
        public static IconService Instance { get; } = new();

        private readonly ConcurrentDictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] PerPathExtensions =
            { ".exe", ".lnk", ".ico", ".msi", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

        public ImageSource? GetLargeIcon(string path)
        {
            var key = CacheKey(path);
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            var image = Extract(path);
            if (image != null)
                _cache[key] = image;
            return image;
        }

        private static string CacheKey(string path)
        {
            var ext = Path.GetExtension(path);
            foreach (var e in PerPathExtensions)
                if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase))
                    return "path::" + path.ToLowerInvariant();
            return "ext::" + ext.ToLowerInvariant();
        }

        private static ImageSource? Extract(string path)
        {
            try { path = System.IO.Path.GetFullPath(path); } catch { /* use as-is */ }

            var shinfo = new NativeMethods.SHFILEINFO();
            uint flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON;

            bool exists = File.Exists(path);
            if (!exists)
                flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;

            _ = NativeMethods.SHGetFileInfo(
                path,
                exists ? 0 : NativeMethods.FILE_ATTRIBUTE_NORMAL,
                ref shinfo,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf(shinfo),
                flags);

            if (shinfo.hIcon == IntPtr.Zero)
                return null;

            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    shinfo.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                NativeMethods.DestroyIcon(shinfo.hIcon);
            }
        }
    }
}

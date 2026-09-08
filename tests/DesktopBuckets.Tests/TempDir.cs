using System;
using System.IO;

namespace DesktopBuckets.Tests
{
    /// <summary>A throw-away directory under %TEMP%, deleted on dispose.</summary>
    internal sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DesktopBuckets.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string File(string name, string contents = "x")
        {
            var p = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllText(p, contents);
            return p;
        }

        /// <summary>Creates a sub-folder directly under this temp dir and returns its path.</summary>
        public string Dir(string name)
        {
            var p = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(p);
            return p;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    // clear Hidden/ReadOnly so Delete(recursive) doesn't choke
                    foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                        System.IO.File.SetAttributes(f, FileAttributes.Normal);
                    foreach (var d in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories))
                        System.IO.File.SetAttributes(d, FileAttributes.Directory);
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch { /* best effort */ }
        }
    }
}

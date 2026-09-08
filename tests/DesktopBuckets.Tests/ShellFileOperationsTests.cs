using System.IO;
using System.Threading.Tasks;
using DesktopBuckets.Interop;
using Xunit;

namespace DesktopBuckets.Tests
{
    public class ShellFileOperationsTests
    {
        [Fact]
        public async Task MovesFilesAndFoldersIntoDestination()
        {
            using var tmp = new TempDir();
            var src = Directory.CreateDirectory(Path.Combine(tmp.Path, "src")).FullName;
            var dest = Directory.CreateDirectory(Path.Combine(tmp.Path, "dest")).FullName;
            var file = Path.Combine(src, "a.txt");
            File.WriteAllText(file, "hello");
            var sub = Directory.CreateDirectory(Path.Combine(src, "sub")).FullName;
            File.WriteAllText(Path.Combine(sub, "deep.txt"), "deep");

            var r = await ShellFileOperations.TransferAsync(new[] { file, sub }, dest, copy: false);

            Assert.True(r.Succeeded, r.Error);
            Assert.False(File.Exists(file));
            Assert.False(Directory.Exists(sub));
            Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "a.txt")));
            Assert.Equal("deep", File.ReadAllText(Path.Combine(dest, "sub", "deep.txt")));
        }

        [Fact]
        public async Task CopyLeavesSourceInPlace()
        {
            using var tmp = new TempDir();
            var dest = Directory.CreateDirectory(Path.Combine(tmp.Path, "dest")).FullName;
            var file = tmp.File("b.txt", "copy me");

            var r = await ShellFileOperations.TransferAsync(new[] { file }, dest, copy: true);

            Assert.True(r.Succeeded, r.Error);
            Assert.True(File.Exists(file));
            Assert.Equal("copy me", File.ReadAllText(Path.Combine(dest, "b.txt")));
        }

        [Fact]
        public async Task CollisionKeepsBothFiles()
        {
            using var tmp = new TempDir();
            var dest = Directory.CreateDirectory(Path.Combine(tmp.Path, "dest")).FullName;
            File.WriteAllText(Path.Combine(dest, "c.txt"), "old");
            var incoming = tmp.File("c.txt", "new");

            var r = await ShellFileOperations.TransferAsync(new[] { incoming }, dest, copy: false);

            Assert.True(r.Succeeded, r.Error);
            Assert.Equal("old", File.ReadAllText(Path.Combine(dest, "c.txt")));
            var files = Directory.GetFiles(dest);
            Assert.Equal(2, files.Length);
            Assert.False(File.Exists(incoming));
        }

        [Fact]
        public async Task MissingSourceReportsAnErrorInsteadOfThrowing()
        {
            using var tmp = new TempDir();
            var dest = Directory.CreateDirectory(Path.Combine(tmp.Path, "dest")).FullName;

            var r = await ShellFileOperations.TransferAsync(new[] { Path.Combine(tmp.Path, "nope.txt") }, dest, copy: false);

            Assert.False(r.Succeeded);
            Assert.False(r.Aborted);
            Assert.NotNull(r.Error);
        }
    }
}

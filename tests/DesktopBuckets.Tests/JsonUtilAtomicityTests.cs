using System.IO;
using DesktopBuckets.Services;
using Xunit;

namespace DesktopBuckets.Tests
{
    public class JsonUtilAtomicityTests
    {
        private sealed class Doc { public string Name { get; set; } = ""; }

        [Fact]
        public void WriteFailureIsReportedNotThrownAndLeavesOldFile()
        {
            using var tmp = new TempDir();
            var path = Path.Combine(tmp.Path, "d.json");
            Assert.True(JsonUtil.WriteHidden(path, new Doc { Name = "old" }));

            // Hold the target open with no sharing so the rename over it must fail.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.False(JsonUtil.Write(path, new Doc { Name = "new" }));
            }

            Assert.Equal("old", JsonUtil.Read<Doc>(path)!.Name);
            Assert.False(File.Exists(path + ".tmp"));
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden), "old file should stay hidden after a failed save");
        }

        [Fact]
        public void ReadOnlyTargetIsReplaced()
        {
            using var tmp = new TempDir();
            var path = Path.Combine(tmp.Path, "d.json");
            JsonUtil.Write(path, new Doc { Name = "1" });
            File.SetAttributes(path, FileAttributes.ReadOnly);

            Assert.True(JsonUtil.Write(path, new Doc { Name = "2" }));
            Assert.Equal("2", JsonUtil.Read<Doc>(path)!.Name);
        }
    }
}

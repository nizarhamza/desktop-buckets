using System;
using System.IO;
using DesktopBuckets.Services;
using Xunit;

namespace DesktopBuckets.Tests
{
    public class StaleDownloadCleanupTests
    {
        [Fact]
        public void RemovesOldDownloadFoldersAndKeepsRecentOnes()
        {
            using var tmp = new TempDir();
            var old = Directory.CreateDirectory(Path.Combine(tmp.Path, UpdateService.DownloadDirPrefix + "old")).FullName;
            File.WriteAllText(Path.Combine(old, "Setup.exe"), "x");
            Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-3));

            var fresh = Directory.CreateDirectory(Path.Combine(tmp.Path, UpdateService.DownloadDirPrefix + "new")).FullName;
            File.WriteAllText(Path.Combine(fresh, "Setup.exe"), "x");

            var unrelated = Directory.CreateDirectory(Path.Combine(tmp.Path, "SomethingElse")).FullName;
            Directory.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddHours(-3));

            int removed = UpdateService.CleanStaleDownloads(tmp.Path, TimeSpan.FromHours(1));

            Assert.Equal(1, removed);
            Assert.False(Directory.Exists(old));
            Assert.True(Directory.Exists(fresh));
            Assert.True(Directory.Exists(unrelated));
        }

        [Fact]
        public void MissingRootIsANoOp()
        {
            Assert.Equal(0, UpdateService.CleanStaleDownloads(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), TimeSpan.Zero));
        }
    }
}

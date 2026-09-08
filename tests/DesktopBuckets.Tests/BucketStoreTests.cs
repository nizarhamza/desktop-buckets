using System.IO;
using System.Linq;
using DesktopBuckets.Services;
using Xunit;

namespace DesktopBuckets.Tests
{
    public class BucketStoreTests
    {
        [Theory]
        [InlineData(@"C:\Users\x\Desktop Buckets\Work", @"C:\")]
        [InlineData(@"D:\stuff", @"D:\")]
        [InlineData(@"\\nas\share\buckets\Work", @"\\nas\share\")]
        [InlineData(@"\\nas\share", @"\\nas\share\")]
        public void VolumeRootIsTheDriveOrShare(string folder, string expected)
        {
            Assert.Equal(expected, BucketStore.VolumeRoot(folder));
        }

        [Fact]
        public void VolumeRootIsNullForRelativeOrBareServerPaths()
        {
            Assert.Null(BucketStore.VolumeRoot("relative\\path"));
            Assert.Null(BucketStore.VolumeRoot(@"\\server"));
        }

        [Fact]
        public void ExistingFolderIsNotOffline()
        {
            using var tmp = new TempDir();
            Assert.False(BucketStore.IsOffline(tmp.Path));
        }

        [Fact]
        public void MissingFolderOnPresentVolumeIsNotOffline()
        {
            using var tmp = new TempDir();
            Assert.False(BucketStore.IsOffline(Path.Combine(tmp.Path, "gone")));
        }

        [Fact]
        public void MissingFolderOnMissingDriveIsOffline()
        {
            // Pick a drive letter that isn't mapped on this machine.
            var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
            var free = "ZYXWVUTSRQPONMLKJIHGFE".First(c => !used.Contains(c));
            Assert.True(BucketStore.IsOffline($@"{free}:\Desktop Buckets\Work"));
        }
    }
}

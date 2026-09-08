using System.IO;
using System.Linq;
using DesktopBuckets.Models;
using DesktopBuckets.ViewModels;
using Xunit;

namespace DesktopBuckets.Tests
{
    public class BucketTileViewModelTests
    {
        [Fact]
        public void RefreshKeepsUnchangedSlotInstances()
        {
            using var tmp = new TempDir();
            tmp.File("a.txt");
            tmp.File("b.txt");
            var vm = new BucketTileViewModel(Bucket.LoadOrCreate(tmp.Path));
            var before = vm.Slots.ToArray();
            Assert.Equal(2, before.Length);

            vm.Refresh();

            Assert.Equal(before, vm.Slots.ToArray()); // same instances, same order
        }

        [Fact]
        public void RefreshReplacesOnlyWhatChanged()
        {
            using var tmp = new TempDir();
            var a = tmp.File("a.txt");
            var b = tmp.File("b.txt");
            // deterministic activity order: a newer than b
            File.SetLastWriteTimeUtc(b, new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(a, new System.DateTime(2026, 1, 2, 0, 0, 0, System.DateTimeKind.Utc));
            var bucket = Bucket.LoadOrCreate(tmp.Path);
            var vm = new BucketTileViewModel(bucket);
            Assert.Equal(new[] { "a.txt", "b.txt" }, vm.Slots.Select(s => s.Name));
            var aVm = vm.Slots[0];
            var bVm = vm.Slots[1];

            bucket.Pin("a.txt"); // a stays first but its pin state changes; b is untouched
            vm.Refresh();

            Assert.Equal(new[] { "a.txt", "b.txt" }, vm.Slots.Select(s => s.Name));
            Assert.NotSame(aVm, vm.Slots[0]);   // pin state changed -> new VM
            Assert.True(vm.Slots[0].IsPinned);
            Assert.Same(bVm, vm.Slots[1]);      // unchanged -> same instance, container kept
        }

        [Fact]
        public void RefreshShrinksAndGrowsWithTheFolder()
        {
            using var tmp = new TempDir();
            var a = tmp.File("a.txt");
            var vm = new BucketTileViewModel(Bucket.LoadOrCreate(tmp.Path));
            Assert.Single(vm.Slots);

            File.Delete(a);
            vm.Refresh();
            Assert.Empty(vm.Slots);
            Assert.True(vm.IsEmpty);

            tmp.File("c.txt");
            tmp.File("d.txt");
            vm.Refresh();
            Assert.Equal(2, vm.Slots.Count);
            Assert.False(vm.IsEmpty);
        }
    }
}

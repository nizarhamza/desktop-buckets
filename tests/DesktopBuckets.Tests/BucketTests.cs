using System;
using System.IO;
using System.Linq;
using DesktopBuckets.Models;
using Xunit;

namespace DesktopBuckets.Tests
{
    public class BucketTests
    {
        // ---- SanitizeName ---------------------------------------------

        [Theory]
        [InlineData("  Work  ", "Work")]
        [InlineData("a/b\\c:d*e?f\"g<h>i|j", "abcdefghij")]
        [InlineData("trailing...", "trailing")]
        [InlineData("", "")]
        [InlineData("   ", "")]
        public void SanitizeNameStripsInvalidCharsAndTrims(string input, string expected)
        {
            Assert.Equal(expected, Bucket.SanitizeName(input));
        }

        [Theory]
        [InlineData("CON")]
        [InlineData("con")]
        [InlineData("NUL")]
        [InlineData("COM1")]
        [InlineData("LPT9")]
        [InlineData("aux.txt")]
        public void SanitizeNameAvoidsReservedDeviceNames(string input)
        {
            var name = Bucket.SanitizeName(input);
            Assert.NotEqual(input, name);
            Assert.NotEmpty(name);
            // and the result must be creatable
            using var tmp = new TempDir();
            var dir = Directory.CreateDirectory(Path.Combine(tmp.Path, name));
            Assert.True(dir.Exists);
        }

        [Theory]
        [InlineData("CONTOSO")]
        [InlineData("COM10")]
        [InlineData("Console")]
        public void SanitizeNameLeavesNearMissesAlone(string input)
        {
            Assert.Equal(input, Bucket.SanitizeName(input));
        }

        // ---- pin / unpin ----------------------------------------------

        [Fact]
        public void PinAppendsInOrderAndUnpinRemovesCaseInsensitively()
        {
            using var tmp = new TempDir();
            var b = Bucket.LoadOrCreate(tmp.Path);

            b.Pin("b.txt");
            b.Pin("a.txt");
            b.Pin("B.TXT"); // duplicate, different case
            Assert.Equal(new[] { "b.txt", "a.txt" }, b.Config.Pinned);
            Assert.True(b.IsPinned("A.TXT"));

            b.Unpin("B.txt");
            Assert.Equal(new[] { "a.txt" }, b.Config.Pinned);
        }

        [Fact]
        public void PinsPersistToTheHiddenConfigFile()
        {
            using var tmp = new TempDir();
            var b = Bucket.LoadOrCreate(tmp.Path);
            b.Pin("x.txt");

            Assert.True(File.Exists(b.ConfigPath));
            Assert.True(File.GetAttributes(b.ConfigPath).HasFlag(FileAttributes.Hidden));

            var again = Bucket.LoadOrCreate(tmp.Path);
            Assert.Equal(new[] { "x.txt" }, again.Config.Pinned);
            Assert.Equal(b.Id, again.Id);
        }

        // ---- prune ------------------------------------------------------

        [Fact]
        public void PrunePinnedDropsMissingFilesAndStaleOpenTimes()
        {
            using var tmp = new TempDir();
            tmp.File("keep.txt");
            var b = Bucket.LoadOrCreate(tmp.Path);
            b.Pin("keep.txt");
            b.Pin("gone.txt");
            b.Config.LastOpenedUtc["keep.txt"] = DateTime.UtcNow;
            b.Config.LastOpenedUtc["gone.txt"] = DateTime.UtcNow;

            b.PrunePinned();

            Assert.Equal(new[] { "keep.txt" }, b.Config.Pinned);
            Assert.Equal(new[] { "keep.txt" }, b.Config.LastOpenedUtc.Keys);
        }

        // ---- enumeration -----------------------------------------------

        [Fact]
        public void EnumerateFilesSkipsConfigDotfilesAndJunk()
        {
            using var tmp = new TempDir();
            tmp.File("doc.txt");
            tmp.File(".hidden-dotfile");
            tmp.File("old.bak");
            tmp.File("scratch.tmp");
            tmp.File("desktop.ini");
            var hidden = tmp.File("hidden.txt");
            File.SetAttributes(hidden, FileAttributes.Hidden);
            Directory.CreateDirectory(Path.Combine(tmp.Path, "subdir"));

            var b = Bucket.LoadOrCreate(tmp.Path); // writes .bucket.json
            var names = b.EnumerateFiles().Select(f => f.Name).ToArray();

            Assert.Equal(new[] { "doc.txt" }, names);
        }

        [Fact]
        public void EnumerateFilesReportsPinStateAndOpenTime()
        {
            using var tmp = new TempDir();
            tmp.File("a.txt");
            var b = Bucket.LoadOrCreate(tmp.Path);
            b.Pin("A.TXT");
            b.RecordOpened("a.txt");

            var f = Assert.Single(b.EnumerateFiles());
            Assert.True(f.IsPinned);
            Assert.True(f.LastOpenedViaTileUtc > DateTime.MinValue);
            Assert.Equal("a.txt", f.RelativePath);
        }

        [Fact]
        public void EnumerateFilesOnMissingFolderIsEmptyNotThrow()
        {
            var b = new Bucket(@"C:\definitely\not\here\" + Guid.NewGuid().ToString("N"), new BucketConfig());
            Assert.Empty(b.EnumerateFiles());
        }

        // ---- rename -----------------------------------------------------

        [Fact]
        public void RenameMovesFolderUnlessLocked()
        {
            using var tmp = new TempDir();
            var src = Directory.CreateDirectory(Path.Combine(tmp.Path, "Old")).FullName;
            var b = Bucket.LoadOrCreate(src);

            Assert.Equal("New", b.Rename("New"));
            Assert.Equal(Path.Combine(tmp.Path, "New"), b.FolderPath);
            Assert.True(Directory.Exists(b.FolderPath));
            Assert.False(Directory.Exists(src));

            b.Config.Locked = true;
            b.Rename("Other");
            Assert.Equal("Other", b.Name);
            Assert.Equal(Path.Combine(tmp.Path, "New"), b.FolderPath); // folder untouched
        }

        [Fact]
        public void SetSlotCountClampsTo1Through9()
        {
            using var tmp = new TempDir();
            var b = Bucket.LoadOrCreate(tmp.Path);
            b.SetSlotCount(0);
            Assert.Equal(1, b.Config.SlotCount);
            b.SetSlotCount(99);
            Assert.Equal(9, b.Config.SlotCount);
        }
    }
}

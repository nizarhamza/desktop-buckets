using System;
using System.Collections.Generic;
using System.Linq;
using DesktopBuckets.Models;
using DesktopBuckets.Services;
using Xunit;

namespace DesktopBuckets.Tests
{
    public class FileRankingServiceTests
    {
        private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static BucketFile F(string name, int ageMinutes, int openedAgeMinutes = int.MaxValue) => new()
        {
            FullPath = @"C:\b\" + name,
            RelativePath = name,
            Name = name,
            LastWriteUtc = T0.AddMinutes(-ageMinutes),
            LastOpenedViaTileUtc = openedAgeMinutes == int.MaxValue ? DateTime.MinValue : T0.AddMinutes(-openedAgeMinutes),
        };

        private static Bucket B(int slots, params string[] pinned) =>
            new(@"C:\b", new BucketConfig { SlotCount = slots, Pinned = pinned.ToList() });

        private static string[] Names(IReadOnlyList<BucketFile> r) => r.Select(f => f.Name).ToArray();

        [Fact]
        public void PinnedComeFirstInPinOrder()
        {
            var files = new[] { F("a", 1), F("b", 2), F("c", 3), F("d", 4) };
            var r = FileRankingService.SelectVisible(B(4, "c", "b"), files);
            Assert.Equal(new[] { "c", "b", "a", "d" }, Names(r));
        }

        [Fact]
        public void RemainingSlotsFilledByMostRecentActivity()
        {
            var files = new[] { F("old", 100), F("new", 1), F("mid", 50) };
            var r = FileRankingService.SelectVisible(B(3), files);
            Assert.Equal(new[] { "new", "mid", "old" }, Names(r));
        }

        [Fact]
        public void OpenedViaTileCountsAsActivity()
        {
            // "old" was written long ago but opened from the tile just now.
            var files = new[] { F("old", 100, openedAgeMinutes: 0), F("new", 1) };
            var r = FileRankingService.SelectVisible(B(2), files);
            Assert.Equal(new[] { "old", "new" }, Names(r));
        }

        [Fact]
        public void TiesBreakByNameCaseInsensitively()
        {
            var files = new[] { F("b.txt", 5), F("A.txt", 5), F("c.txt", 5) };
            var r = FileRankingService.SelectVisible(B(3), files);
            Assert.Equal(new[] { "A.txt", "b.txt", "c.txt" }, Names(r));
        }

        [Fact]
        public void RespectsSlotCount()
        {
            var files = Enumerable.Range(0, 10).Select(i => F($"f{i}", i)).ToArray();
            var r = FileRankingService.SelectVisible(B(4), files);
            Assert.Equal(4, r.Count);
            Assert.Equal(new[] { "f0", "f1", "f2", "f3" }, Names(r));
        }

        [Fact]
        public void PinsBeyondSlotCountAreTruncated()
        {
            var files = new[] { F("a", 1), F("b", 2), F("c", 3) };
            var r = FileRankingService.SelectVisible(B(2, "c", "b", "a"), files);
            Assert.Equal(new[] { "c", "b" }, Names(r));
        }

        [Fact]
        public void MissingPinsAreIgnored()
        {
            var files = new[] { F("a", 1) };
            var r = FileRankingService.SelectVisible(B(2, "gone", "a"), files);
            Assert.Equal(new[] { "a" }, Names(r));
        }

        [Fact]
        public void PinMatchIsCaseInsensitive()
        {
            var files = new[] { F("Report.docx", 1), F("z", 0) };
            var r = FileRankingService.SelectVisible(B(2, "report.DOCX"), files);
            Assert.Equal(new[] { "Report.docx", "z" }, Names(r));
        }

        [Fact]
        public void ZeroSlotsOrNoFilesGivesEmpty()
        {
            Assert.Empty(FileRankingService.SelectVisible(B(0), new[] { F("a", 1) }));
            Assert.Empty(FileRankingService.SelectVisible(B(4), Array.Empty<BucketFile>()));
        }

        [Fact]
        public void FewerFilesThanSlotsReturnsWhatExists()
        {
            var r = FileRankingService.SelectVisible(B(9), new[] { F("a", 1), F("b", 2) });
            Assert.Equal(2, r.Count);
        }

        [Fact]
        public void DuplicateNamesDifferingByCaseDoNotThrow()
        {
            // Possible on a case-sensitive directory (Windows 10+ per-directory flag).
            var files = new[] { F("Notes.txt", 1), F("notes.txt", 2) };
            var r = FileRankingService.SelectVisible(B(4, "notes.txt"), files);
            // Ranking is case-insensitive by design, so the two collapse to one slot;
            // what matters is that ToDictionary's duplicate-key ArgumentException is gone.
            Assert.Single(r);
        }
    }
}

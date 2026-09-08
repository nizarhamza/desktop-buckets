using System.Collections.Generic;
using System.Windows;
using DesktopBuckets.Interop;
using Xunit;

namespace DesktopBuckets.Tests
{
    /// <summary>
    /// Exercises DesktopShell.FindNonOverlappingPosition directly. This is the fix for
    /// the "buckets overlapping" bug reported live: a freshly placed (or already stored)
    /// tile position could sit on top of another live tile with no avoidance at all —
    /// visible as two tiles' cards and labels literally drawn over each other, and (the
    /// worse half) the overlapping tile's own MakeSpace later treating the desktop cells
    /// hidden behind its neighbour as free and shoving real icons there.
    /// </summary>
    public class FindNonOverlappingPositionTests
    {
        private static readonly Size Step = new(32, 32);
        private static bool AlwaysOnScreen(double x, double y) => true;

        /// <summary>Mirrors DesktopShell's private StrictlyOverlaps: merely touching at
        /// an edge is not overlap. Assertions use this, not Rect.IntersectsWith, which
        /// treats a shared boundary as overlapping — a stricter bar than the production
        /// code actually needs to clear.</summary>
        private static bool StrictlyOverlaps(Rect a, Rect b) =>
            a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

        [Fact]
        public void NoObstaclesReturnsNull()
        {
            var mine = new Rect(100, 100, 64, 64);
            var result = DesktopShell.FindNonOverlappingPosition(mine, new List<Rect>(), Step, AlwaysOnScreen);
            Assert.Null(result);
        }

        [Fact]
        public void NonOverlappingObstacleReturnsNull()
        {
            var mine = new Rect(100, 100, 64, 64);
            var obstacles = new List<Rect> { new(300, 300, 64, 64) };
            var result = DesktopShell.FindNonOverlappingPosition(mine, obstacles, Step, AlwaysOnScreen);
            Assert.Null(result);
        }

        [Fact]
        public void EdgeAdjacentTilesAreNotConsideredOverlapping()
        {
            // Touching but not overlapping (right edge of mine == left edge of obstacle)
            // must be left alone — deliberately placed adjacent tiles shouldn't get nudged.
            var mine = new Rect(100, 100, 64, 64);
            var obstacles = new List<Rect> { new(164, 100, 64, 64) };
            var result = DesktopShell.FindNonOverlappingPosition(mine, obstacles, Step, AlwaysOnScreen);
            Assert.Null(result);
        }

        [Fact]
        public void FullyOverlappingTilesAreSeparated()
        {
            // The exact reported bug: two tiles at the same spot, labels garbled together.
            var mine = new Rect(100, 100, 64, 64);
            var obstacles = new List<Rect> { new(100, 100, 64, 64) };
            var result = DesktopShell.FindNonOverlappingPosition(mine, obstacles, Step, AlwaysOnScreen);

            Assert.NotNull(result);
            var moved = new Rect(result!.Value.X, result.Value.Y, 64, 64);
            Assert.False(StrictlyOverlaps(moved, obstacles[0]));
        }

        [Fact]
        public void PartiallyOverlappingTilesAreSeparated()
        {
            var mine = new Rect(100, 100, 64, 64);
            var obstacles = new List<Rect> { new(130, 100, 64, 64) }; // overlaps by 34px
            var result = DesktopShell.FindNonOverlappingPosition(mine, obstacles, Step, AlwaysOnScreen);

            Assert.NotNull(result);
            var moved = new Rect(result!.Value.X, result.Value.Y, 64, 64);
            Assert.False(StrictlyOverlaps(moved, obstacles[0]));
        }

        [Fact]
        public void ResolvedPositionIsTheNearestFreeSpot()
        {
            // Ring search from the origin: the very first ring (one step away) already
            // clears a single small obstacle directly on top, so the fix should be a
            // small nudge, not a distant relocation across the desktop.
            var mine = new Rect(100, 100, 64, 64);
            var obstacles = new List<Rect> { new(100, 100, 64, 64) };
            var result = DesktopShell.FindNonOverlappingPosition(mine, obstacles, Step, AlwaysOnScreen);

            Assert.NotNull(result);
            double dx = System.Math.Abs(result!.Value.X - 100);
            double dy = System.Math.Abs(result.Value.Y - 100);
            Assert.True(dx <= Step.Width * 2 && dy <= Step.Height * 2,
                $"resolved position moved unexpectedly far: ({result.Value.X},{result.Value.Y})");
        }

        [Fact]
        public void MovesAwayFromEveryObstacleAtOnce()
        {
            // Surrounded on three sides — the free ring position must clear ALL of them,
            // not just whichever one happens first.
            var mine = new Rect(100, 100, 64, 64);
            var obstacles = new List<Rect>
            {
                new(100, 100, 64, 64),   // dead center, same as mine
                new(164, 100, 30, 64),   // just to the right of a naive first escape
                new(68, 100, 30, 64),    // just to the left
            };
            var result = DesktopShell.FindNonOverlappingPosition(mine, obstacles, Step, AlwaysOnScreen);

            Assert.NotNull(result);
            var moved = new Rect(result!.Value.X, result.Value.Y, 64, 64);
            foreach (var o in obstacles)
                Assert.False(StrictlyOverlaps(moved, o), $"still overlaps obstacle {o}");
        }

        [Fact]
        public void OffScreenCandidatesAreSkipped()
        {
            // Restrict "on screen" to a thin vertical strip that still leaves room
            // above/below the obstacle — the result must respect that constraint.
            var mine = new Rect(0, 0, 64, 64);
            var obstacles = new List<Rect> { new(0, 0, 64, 64) };
            bool OnlyStraightDown(double x, double y) => x == 0 && y >= 0;

            var result = DesktopShell.FindNonOverlappingPosition(mine, obstacles, Step, OnlyStraightDown);

            Assert.NotNull(result);
            Assert.Equal(0, result!.Value.X);
            Assert.True(result.Value.Y >= 0);
        }

        [Fact]
        public void NoFreeSpotWithinRingLimitReturnsNull()
        {
            // maxRing=1 with the target boxed in on every side within that single ring:
            // no candidate in the (only) ring clears every obstacle, so it gives up
            // rather than picking a position that still overlaps something.
            var mine = new Rect(0, 0, 64, 64);
            var obstacles = new List<Rect>();
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    obstacles.Add(new Rect(dx * 32, dy * 32, 64, 64));

            var result = DesktopShell.FindNonOverlappingPosition(mine, obstacles, Step, AlwaysOnScreen, maxRing: 1);
            Assert.Null(result);
        }

        [Fact]
        public void ZeroSizeRectReturnsNull()
        {
            var mine = new Rect(100, 100, 0, 0);
            var obstacles = new List<Rect> { new(100, 100, 64, 64) };
            var result = DesktopShell.FindNonOverlappingPosition(mine, obstacles, Step, AlwaysOnScreen);
            Assert.Null(result);
        }
    }
}

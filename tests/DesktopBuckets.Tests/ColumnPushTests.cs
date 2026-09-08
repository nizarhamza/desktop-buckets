using System.Collections.Generic;
using System.Linq;
using DesktopBuckets.Interop;
using Xunit;

namespace DesktopBuckets.Tests
{
    /// <summary>
    /// Exercises DesktopShell.ComputeColumnPush directly — no OS calls, no live desktop
    /// — so these are fast and immune to the kind of desktop-state contamination that
    /// makes repeated end-to-end drag tests unreliable.
    /// </summary>
    public class ColumnPushTests
    {
        // ---- invariant checkers, reused across scenarios ------------------

        private static void AssertNoneLandInBlocked(
            DesktopShell.ColumnPushResult result, int tileLeft, int tileRight, int tileTop, int tileBottom)
        {
            foreach (var (idx, to, _) in result)
                Assert.False(to.col >= tileLeft && to.col <= tileRight && to.row >= tileTop && to.row <= tileBottom,
                    $"icon {idx} landed at {to}, inside the blocked band cols[{tileLeft}..{tileRight}] rows[{tileTop}..{tileBottom}]");
        }

        private static void AssertNoneLeftOfTile(DesktopShell.ColumnPushResult result, int tileLeft)
        {
            foreach (var (idx, to, _) in result)
                Assert.True(to.col >= tileLeft, $"icon {idx} landed at column {to.col}, left of the tile (tileLeft={tileLeft})");
        }

        /// <summary>Icons still in <see cref="DesktopShell.ColumnPushResult.Overflow"/>
        /// have been displaced but not yet placed anywhere (the caller resolves that via
        /// FindFreeCell) — they have no definite final cell yet, so they're excluded
        /// entirely here rather than assumed to still be at their original position
        /// (that wrong assumption is exactly what let a real bug through: an overflowed
        /// icon's old cell is routinely reclaimed by whatever pushed it out).</summary>
        private static void AssertNoTwoIconsShareACell(DesktopShell.ColumnPushResult result,
            IReadOnlyDictionary<int, (int col, int row)> original)
        {
            var overflow = new HashSet<int>(result.Overflow);
            var finalCell = new Dictionary<int, (int col, int row)>();
            foreach (var kv in original)
                if (!overflow.Contains(kv.Key)) finalCell[kv.Key] = kv.Value; // default: untouched, stays put
            foreach (var (idx, to, _) in result) finalCell[idx] = to;          // moved: overrides the default

            var seen = new Dictionary<(int, int), int>();
            foreach (var kv in finalCell)
            {
                Assert.False(seen.TryGetValue(kv.Value, out var other),
                    $"icons {(seen.TryGetValue(kv.Value, out var o) ? o : -1)} and {kv.Key} both end up at cell {kv.Value}");
                seen[kv.Value] = kv.Key;
            }
        }

        private static void AssertNoIconLost(DesktopShell.ColumnPushResult result,
            IReadOnlyDictionary<int, (int col, int row)> original, int maxCol, int maxRow)
        {
            var finalCell = new Dictionary<int, (int col, int row)>(original);
            foreach (var (idx, to, _) in result) finalCell[idx] = to;
            foreach (var kv in finalCell)
                Assert.True(kv.Value.col >= 0 && kv.Value.col <= maxCol && kv.Value.row >= 0 && kv.Value.row <= maxRow,
                    $"icon {kv.Key} ended up off-grid at {kv.Value}");
        }

        // ---- OccupiedAfterMoves: the actual bug this file found -----------

        [Fact]
        public void OccupiedAfterMoves_HandlesAnIconMovingIntoAnotherMovedIconsOldCell()
        {
            // A moves from (0,0) to (1,0) — B's old cell. B moves from (1,0) to (2,0).
            // The naive "Remove(before[idx]) then Add(to), per move in list order" gets
            // this wrong when B is processed after A: it removes (1,0) again even though
            // A just moved there, leaving (1,0) looking free.
            var before = new Dictionary<int, (int, int)> { [1] = (0, 0), [2] = (1, 0) };
            var moves = new List<(int, (int, int), int)> { (1, (1, 0), 0), (2, (2, 0), 0) };

            var occ = DesktopShell.OccupiedAfterMoves(before, moves);

            Assert.Contains((1, 0), occ);   // A is here now
            Assert.Contains((2, 0), occ);   // B is here now
            Assert.DoesNotContain((0, 0), occ); // A's old cell is genuinely empty
            Assert.Equal(2, occ.Count);
        }

        [Fact]
        public void OccupiedAfterMoves_UnmovedIconsKeepTheirCell()
        {
            var before = new Dictionary<int, (int, int)> { [1] = (0, 0), [2] = (5, 5) };
            var moves = new List<(int, (int, int), int)> { (1, (1, 0), 0) };
            var occ = DesktopShell.OccupiedAfterMoves(before, moves);
            Assert.Equal(new HashSet<(int, int)> { (1, 0), (5, 5) }, occ);
        }

        // ---- small, hand-worked scenarios ---------------------------------

        [Fact]
        public void EmptyGridProducesNoMoves()
        {
            var result = DesktopShell.ComputeColumnPush(
                new Dictionary<int, (int, int)>(), tileLeft: 3, tileRight: 4, tileTop: 2, tileBottom: 3, maxCol: 12, maxRow: 9);
            Assert.Empty(result.Placements);
            Assert.Empty(result.Overflow);
        }

        [Fact]
        public void IconsAboveTileNeverMove()
        {
            var icons = new Dictionary<int, (int, int)> { [1] = (3, 0), [2] = (4, 1) }; // both above tileTop=2
            var result = DesktopShell.ComputeColumnPush(icons, tileLeft: 3, tileRight: 4, tileTop: 2, tileBottom: 3, maxCol: 12, maxRow: 9);
            Assert.Empty(result.Placements);
        }

        [Fact]
        public void IconsLeftOfTileNeverPassedIn_ButDefendedAnyway()
        {
            // Even if the caller mistakenly includes one left of tileLeft, it's ignored.
            var icons = new Dictionary<int, (int, int)> { [1] = (0, 5) };
            var result = DesktopShell.ComputeColumnPush(icons, tileLeft: 3, tileRight: 4, tileTop: 2, tileBottom: 3, maxCol: 12, maxRow: 9);
            Assert.Empty(result.Placements);
        }

        [Fact]
        public void SingleIconUnderTileMovesToJustBelowIt()
        {
            var icons = new Dictionary<int, (int, int)> { [1] = (3, 2) }; // inside blocked band
            var result = DesktopShell.ComputeColumnPush(icons, tileLeft: 3, tileRight: 4, tileTop: 2, tileBottom: 3, maxCol: 12, maxRow: 9);
            var move = Assert.Single(result.Placements);
            Assert.Equal((3, 4), move.to); // tileBottom+1
        }

        [Fact]
        public void IconsBelowTileAlsoShiftDownToStayContiguous()
        {
            // Column 3: icon at row 2 (blocked) and row 4 (already clear, but part of the
            // "at or after the insertion point" run) — both shift down by the band height.
            var icons = new Dictionary<int, (int, int)> { [1] = (3, 2), [2] = (3, 4) };
            var result = DesktopShell.ComputeColumnPush(icons, tileLeft: 3, tileRight: 4, tileTop: 2, tileBottom: 3, maxCol: 12, maxRow: 9);
            var m1 = result.Placements.Single(p => p.idx == 1);
            var m2 = result.Placements.Single(p => p.idx == 2);
            Assert.Equal((3, 4), m1.to);
            Assert.Equal((3, 5), m2.to);
            AssertNoneLandInBlocked(result, 3, 4, 2, 3);
        }

        [Fact]
        public void OverflowCarriesToTheNextColumnLandingAtItsTop()
        {
            // Column 3 (a tile column) is nearly full below the tile: rows 4..9 already
            // occupied, plus one icon to push out of the blocked band -> one must overflow.
            var icons = new Dictionary<int, (int, int)>();
            icons[100] = (3, 2); // under the tile, must move
            for (int r = 4; r <= 9; r++) icons[r] = (3, r); // fills the rest of column 3 below the tile
            var result = DesktopShell.ComputeColumnPush(icons, tileLeft: 3, tileRight: 4, tileTop: 2, tileBottom: 3, maxCol: 12, maxRow: 9);

            AssertNoneLandInBlocked(result, 3, 4, 2, 3);
            AssertNoTwoIconsShareACell(result, icons);
            // Column 4 (also a tile column) should receive the carried-over icon, landing
            // just below the tile there too (its own rows 2-3 are blocked as well).
            var carried = result.Placements.Where(p => p.to.col == 4).ToList();
            Assert.NotEmpty(carried);
            Assert.All(carried, p => Assert.True(p.to.row > 3));
        }

        [Fact]
        public void TileSpanningTwoColumnsClearsBoth()
        {
            var icons = new Dictionary<int, (int, int)>
            {
                [1] = (3, 2), [2] = (3, 3), [3] = (4, 2), [4] = (4, 3), // all four under the tile
            };
            var result = DesktopShell.ComputeColumnPush(icons, tileLeft: 3, tileRight: 4, tileTop: 2, tileBottom: 3, maxCol: 12, maxRow: 9);
            Assert.Equal(4, result.Placements.Count);
            AssertNoneLandInBlocked(result, 3, 4, 2, 3);
            AssertNoTwoIconsShareACell(result, icons);
        }

        [Fact]
        public void PackedMonitorOverflowsPastMaxCol()
        {
            // Every column from tileLeft to maxCol is completely full; nothing fits
            // anywhere, so everything originally under/after the tile ends up in Overflow.
            var icons = new Dictionary<int, (int, int)>();
            for (int c = 3; c <= 5; c++)
            for (int r = 0; r <= 2; r++)
                icons[c * 10 + r] = (c, r);
            var result = DesktopShell.ComputeColumnPush(icons, tileLeft: 3, tileRight: 3, tileTop: 0, tileBottom: 2, maxCol: 5, maxRow: 2);
            AssertNoneLandInBlocked(result, 3, 3, 0, 2);
            Assert.NotEmpty(result.Overflow);
            // Nothing placed should collide with icons that never moved (cols 4-5, cols
            // left untouched by this scenario's math) or with each other.
            AssertNoTwoIconsShareACell(result, icons);
        }

        // ---- the scenario that actually matters: a dense real-world desktop -----

        /// <summary>Builds a layout matching what --dump-grid reported on the test
        /// machine: 13 columns x 10 rows, 116 of 130 cells occupied, in reading order
        /// (column-major) leaving a handful of gaps scattered through — a genuinely messy
        /// desktop, not an artificially tidy one.</summary>
        private static Dictionary<int, (int col, int row)> RealisticDesktop(int maxCol, int maxRow, int gapEvery)
        {
            var icons = new Dictionary<int, (int, int)>();
            int idx = 0;
            for (int c = 0; c <= maxCol; c++)
            for (int r = 0; r <= maxRow; r++)
            {
                int n = c * (maxRow + 1) + r;
                if (n % gapEvery == gapEvery - 1) continue; // scattered gaps, like a real desktop
                icons[idx++] = (c, r);
            }
            return icons;
        }

        public static IEnumerable<object[]> RealisticTilePositions()
        {
            // (tileLeft, tileTop) for a 2x2 tile, swept across a 13x10 grid including
            // edges and the exact area the live desktop test flagged (cols 5-6, rows 4-5).
            foreach (var (l, t) in new[] { (0, 0), (5, 4), (6, 3), (11, 8), (12, 9), (3, 0), (0, 8) })
                yield return new object[] { l, t };
        }

        [Theory]
        [MemberData(nameof(RealisticTilePositions))]
        public void RealisticDenseDesktop_NoIconEverEndsUpUnderTheTile(int tileLeft, int tileTop)
        {
            const int maxCol = 12, maxRow = 9; // 13x10, matches the machine this was found on
            var icons = RealisticDesktop(maxCol, maxRow, gapEvery: 7);
            int tileRight = System.Math.Min(maxCol, tileLeft + 1);   // 2 columns wide
            int tileBottom = System.Math.Min(maxRow, tileTop + 1);   // 2 rows tall

            var filtered = icons.Where(kv => kv.Value.col >= tileLeft)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            var result = DesktopShell.ComputeColumnPush(filtered, tileLeft, tileRight, tileTop, tileBottom, maxCol, maxRow);

            AssertNoneLandInBlocked(result, tileLeft, tileRight, tileTop, tileBottom);
            AssertNoneLeftOfTile(result, tileLeft);
            AssertNoTwoIconsShareACell(result, filtered);
            if (result.Overflow.Count == 0)
                AssertNoIconLost(result, filtered, maxCol, maxRow);
        }

        [Fact]
        public void RepeatedPushesConverge_NoIconEverUnderTile()
        {
            // Simulates dragging the tile to the SAME spot twice in a row (the live-app
            // equivalent of MakeSpace firing again without an intervening full restore):
            // apply the result once, then feed the resulting layout back in.
            const int maxCol = 12, maxRow = 9;
            var icons = RealisticDesktop(maxCol, maxRow, gapEvery: 7);
            int tileLeft = 5, tileRight = 6, tileTop = 4, tileBottom = 5;

            for (int pass = 0; pass < 3; pass++)
            {
                var filtered = icons.Where(kv => kv.Value.col >= tileLeft).ToDictionary(kv => kv.Key, kv => kv.Value);
                var result = DesktopShell.ComputeColumnPush(filtered, tileLeft, tileRight, tileTop, tileBottom, maxCol, maxRow);
                AssertNoneLandInBlocked(result, tileLeft, tileRight, tileTop, tileBottom);
                AssertNoTwoIconsShareACell(result, filtered);
                foreach (var (idx, to, _) in result) icons[idx] = to;
            }
        }
    }
}

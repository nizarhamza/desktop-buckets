using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DesktopBuckets.Interop;
using Xunit;

namespace DesktopBuckets.Tests
{
    /// <summary>
    /// Exercises DesktopShell.AvoidReservedCells directly. This is the fix for a real bug
    /// caught live while watching a real drag: MakeSpace's push (ComputeColumnPush) only
    /// knows about the icons ITS OWN tile has displaced — it has no idea another live
    /// tile nearby has already parked icons on some of the very cells it's about to push
    /// into. Two tiles operating independently could each conclude a cell is free and
    /// land two different icons on it — visible as real desktop icons on top of each
    /// other with garbled, interleaved label text (confirmed via --dump-grid showing a
    /// DUPLICATE cell appear during ordinary live dragging near an existing tile).
    /// </summary>
    public class AvoidReservedCellsTests
    {
        private static DesktopShell.IconGrid Grid(int monitorCols = 13, int monitorRows = 10) =>
            new(new List<Rect> { new(0, 0, monitorCols * 96, monitorRows * 100) }, new Size(96, 100), new List<Rect>());

        private static (int col, int row) Cell(int col, int row) => (DesktopShell.IconGrid.Encode(0, col), row);

        private static Rect Bounds => new(0, 0, 13 * 96, 10 * 100);

        [Fact]
        public void EmptyReservedSetLeavesMovesUnchanged()
        {
            var moves = new List<(int idx, (int col, int row) to, int stage)> { (1, Cell(3, 3), 0) };
            var before = new Dictionary<int, (int col, int row)> { [1] = Cell(3, 2) };

            var result = DesktopShell.AvoidReservedCells(moves, before, Grid(), new HashSet<(int, int)>(), Bounds, Rect.Empty);

            Assert.Equal(moves, result);
        }

        [Fact]
        public void MoveNotTouchingAReservedCellIsUnchanged()
        {
            var moves = new List<(int idx, (int col, int row) to, int stage)> { (1, Cell(3, 3), 0) };
            var before = new Dictionary<int, (int col, int row)> { [1] = Cell(3, 2) };
            var reserved = new HashSet<(int, int)> { Cell(9, 9) }; // unrelated cell

            var result = DesktopShell.AvoidReservedCells(moves, before, Grid(), reserved, Bounds, Rect.Empty);

            Assert.Equal(moves, result);
        }

        [Fact]
        public void MoveLandingOnAReservedCellIsRedirected()
        {
            // The exact bug: this tile's push computed (5,5) as icon 1's destination,
            // but another tile already parked an icon there.
            var moves = new List<(int idx, (int col, int row) to, int stage)> { (1, Cell(5, 5), 0) };
            var before = new Dictionary<int, (int col, int row)> { [1] = Cell(5, 4) };
            var reserved = new HashSet<(int, int)> { Cell(5, 5) };

            var result = DesktopShell.AvoidReservedCells(moves, before, Grid(), reserved, Bounds, Rect.Empty);

            Assert.Single(result);
            Assert.NotEqual(Cell(5, 5), result[0].to);       // moved off the contested cell
            Assert.DoesNotContain(result[0].to, reserved);    // and didn't land on any other reserved cell
            Assert.Equal(1, result[0].idx);
        }

        [Fact]
        public void RedirectedIconLandsNearItsOwnPrePushCellNotTheContestedOne()
        {
            var moves = new List<(int idx, (int col, int row) to, int stage)> { (1, Cell(8, 8), 0) };
            var before = new Dictionary<int, (int col, int row)> { [1] = Cell(2, 2) }; // far from the contested cell
            var reserved = new HashSet<(int, int)> { Cell(8, 8) };

            var result = DesktopShell.AvoidReservedCells(moves, before, Grid(), reserved, Bounds, Rect.Empty);

            // FindFreeCell searches outward from `before`, not from the contested `to` —
            // the redirected landing should be near (2,2), not near (8,8).
            int dCol = System.Math.Abs(DesktopShell.IconGrid.LocalCol(result[0].to.col) - 2);
            int dRow = System.Math.Abs(result[0].to.row - 2);
            Assert.True(dCol <= 2 && dRow <= 2, $"redirected icon landed far from its own pre-push cell: {result[0].to}");
        }

        [Fact]
        public void RedirectNeverCollidesWithAnotherMoveInTheSameBatch()
        {
            // Two of THIS tile's own icons pushed toward the same contested cell region;
            // redirecting one must not land it on the other's own (already-taken) spot.
            var moves = new List<(int idx, (int col, int row) to, int stage)>
            {
                (1, Cell(5, 5), 0),
                (2, Cell(5, 6), 0),
            };
            var before = new Dictionary<int, (int col, int row)> { [1] = Cell(5, 4), [2] = Cell(5, 3) };
            var reserved = new HashSet<(int, int)> { Cell(5, 5) };

            var result = DesktopShell.AvoidReservedCells(moves, before, Grid(), reserved, Bounds, Rect.Empty);

            var cells = result.Select(r => r.to).ToList();
            Assert.Equal(cells.Count, cells.Distinct().Count());
            Assert.Contains(Cell(5, 6), cells); // icon 2's untouched move survives as-is
        }

        [Fact]
        public void MultipleReservedCollisionsAllResolveDistinctly()
        {
            var moves = new List<(int idx, (int col, int row) to, int stage)>
            {
                (1, Cell(2, 2), 0),
                (2, Cell(7, 7), 0),
                (3, Cell(9, 1), 0), // not contested, must survive untouched
            };
            var before = new Dictionary<int, (int col, int row)>
            {
                [1] = Cell(2, 1), [2] = Cell(7, 6), [3] = Cell(9, 0),
            };
            var reserved = new HashSet<(int, int)> { Cell(2, 2), Cell(7, 7) };

            var result = DesktopShell.AvoidReservedCells(moves, before, Grid(), reserved, Bounds, Rect.Empty);

            Assert.Equal(3, result.Count);
            foreach (var r in result) Assert.DoesNotContain(r.to, reserved);
            Assert.Contains(result, r => r.idx == 3 && r.to == Cell(9, 1));
        }

        [Fact]
        public void TileFootprintIsAlsoAvoidedWhenRedirecting()
        {
            // The redirected landing must never fall under the tile's own footprint,
            // exactly like every other placement in MakeSpace.
            var moves = new List<(int idx, (int col, int row) to, int stage)> { (1, Cell(5, 5), 0) };
            var before = new Dictionary<int, (int col, int row)> { [1] = Cell(5, 4) };
            var reserved = new HashSet<(int, int)> { Cell(5, 5) };
            var footprint = new Rect(4 * 96, 4 * 100, 3 * 96, 3 * 100); // covers cols 4-6, rows 4-6

            var result = DesktopShell.AvoidReservedCells(moves, before, Grid(), reserved, Bounds, footprint);

            var landedRect = Grid().CellRect(result[0].to.col, result[0].to.row);
            Assert.False(landedRect.IntersectsWith(footprint));
        }
    }
}

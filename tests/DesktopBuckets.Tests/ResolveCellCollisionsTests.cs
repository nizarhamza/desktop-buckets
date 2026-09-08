using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DesktopBuckets.Interop;
using Xunit;

namespace DesktopBuckets.Tests
{
    /// <summary>
    /// Exercises DesktopShell.ResolveCellCollisions directly. This is the fix for a real
    /// bug found live: RealignAllIconsToGrid snapped every icon independently to its own
    /// nearest cell with no collision check, so two icons that started close together
    /// could both round to the SAME cell and land exactly on top of each other — visible
    /// as one icon rendered over another with the two labels' text interleaved/garbled.
    /// </summary>
    public class ResolveCellCollisionsTests
    {
        private static DesktopShell.IconGrid Grid(int monitorCols = 13, int monitorRows = 10) =>
            new(new List<Rect> { new(0, 0, monitorCols * 96, monitorRows * 100) }, new Size(96, 100), new List<Rect>());

        /// <summary>Column values must go through IconGrid.Encode (monitor 0 here) — the
        /// grid folds the monitor index into the column number, so a bare column like 11
        /// is NOT the same value CellTopLeft/MonitorRect expect internally.</summary>
        private static (int col, int row) Cell(int col, int row) => (DesktopShell.IconGrid.Encode(0, col), row);

        [Fact]
        public void NoCollisionsPassesThroughUnchanged()
        {
            var home = new Dictionary<int, (int, int)> { [1] = Cell(2, 3), [2] = Cell(5, 5), [3] = Cell(0, 0) };
            var result = DesktopShell.ResolveCellCollisions(home, Grid());
            Assert.Equal(home, result);
        }

        [Fact]
        public void TwoIconsOnTheSameCellAreSeparated()
        {
            // The exact bug: icons 5 and 6 both compute cell (11,0) as their nearest.
            var home = new Dictionary<int, (int, int)>
            {
                [5] = Cell(11, 0),
                [6] = Cell(11, 0), // collides with 5
                [7] = Cell(11, 1), // untouched, not involved
            };
            var result = DesktopShell.ResolveCellCollisions(home, Grid());

            Assert.Equal(3, result.Count);
            var cells = result.Values.ToList();
            Assert.Equal(cells.Count, cells.Distinct().Count()); // no two icons share a cell
            Assert.Contains(Cell(11, 0), cells); // one of the colliding pair kept the contested cell
            Assert.Equal(Cell(11, 1), result[7]); // the uninvolved icon never moves
        }

        [Fact]
        public void ReHomedIconLandsAdjacentToItsOriginalCell()
        {
            var home = new Dictionary<int, (int, int)> { [1] = Cell(5, 5), [2] = Cell(5, 5) };
            var result = DesktopShell.ResolveCellCollisions(home, Grid());

            var moved = result.Single(kv => kv.Value != Cell(5, 5));
            // FindFreeCell searches outward in rings from the original cell — the
            // re-homed icon should land in the immediate neighbourhood, not far away.
            int dCol = System.Math.Abs(DesktopShell.IconGrid.LocalCol(moved.Value.Item1) - 5);
            int dRow = System.Math.Abs(moved.Value.Item2 - 5);
            Assert.True(dCol <= 2 && dRow <= 2, $"re-homed icon landed unexpectedly far away at {moved.Value}");
        }

        [Fact]
        public void ThreeWayCollisionAllEndUpDistinct()
        {
            var home = new Dictionary<int, (int, int)> { [1] = Cell(7, 7), [2] = Cell(7, 7), [3] = Cell(7, 7) };
            var result = DesktopShell.ResolveCellCollisions(home, Grid());

            Assert.Equal(3, result.Count);
            Assert.Equal(3, result.Values.Distinct().Count());
        }

        [Fact]
        public void MultipleIndependentCollisionsAllResolve()
        {
            var home = new Dictionary<int, (int, int)>
            {
                [1] = Cell(2, 2), [2] = Cell(2, 2),   // collision A
                [3] = Cell(9, 8), [4] = Cell(9, 8),   // collision B, far from A
                [5] = Cell(0, 0),                       // untouched
            };
            var result = DesktopShell.ResolveCellCollisions(home, Grid());

            Assert.Equal(5, result.Count);
            Assert.Equal(5, result.Values.Distinct().Count());
            Assert.Equal(Cell(0, 0), result[5]);
        }

        [Fact]
        public void EmptyInputProducesEmptyOutput()
        {
            var result = DesktopShell.ResolveCellCollisions(new Dictionary<int, (int, int)>(), Grid());
            Assert.Empty(result);
        }
    }
}

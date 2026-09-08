using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DesktopBuckets.Interop;
using Xunit;

namespace DesktopBuckets.Tests
{
    /// <summary>
    /// Stress/fuzz test reproducing a real, persistent (confirmed over 21 minutes of
    /// zero drag activity — not an animation-in-flight artifact) icon collision seen
    /// live on a real desktop after v0.3.14 shipped. Sweeps ComputeColumnPush +
    /// AvoidReservedCells together across many tile positions and reserved-cell
    /// configurations on a dense grid shaped like the real desktop (93 icons, ~20x10),
    /// looking for ANY case where the combined pipeline's own final result either
    /// collides with itself or lands on a reserved cell.
    /// </summary>
    public class CrossTilePushStressTests
    {
        private static DesktopShell.IconGrid Grid(int cols = 20, int rows = 10) =>
            new(new List<Rect> { new(0, 0, cols * 96, rows * 100) }, new Size(96, 100), new List<Rect>());

        private static (int col, int row) Cell(int col, int row) => (DesktopShell.IconGrid.Encode(0, col), row);

        /// <summary>A dense-but-not-full grid roughly matching the real desktop: fills
        /// most cells, leaves a few gaps scattered (deterministic pseudo-random via a
        /// fixed seed so failures are reproducible).</summary>
        private static Dictionary<int, (int col, int row)> DenseIcons(int cols, int rows, double fillFraction, int seed)
        {
            var rnd = new System.Random(seed);
            var cells = new Dictionary<int, (int col, int row)>();
            int idx = 0;
            for (int c = 0; c < cols; c++)
                for (int r = 0; r < rows; r++)
                    if (rnd.NextDouble() < fillFraction)
                        cells[idx++] = Cell(c, r);
            return cells;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void PushPlusCrossTileAvoidanceNeverProducesACollisionOrReservedLanding(int seed)
        {
            const int cols = 20, rows = 10;
            var grid = Grid(cols, rows);
            var bounds = new Rect(0, 0, cols * 96, rows * 100);
            var icons = DenseIcons(cols, rows, 0.75, seed);

            // Sweep every plausible tile footprint (1x2 up to 3x3, the app's actual
            // allowed shapes) across a spread of positions.
            var shapes = new (int w, int h)[] { (2, 1), (3, 1), (2, 2), (3, 2), (3, 3) };

            foreach (var (w, h) in shapes)
            {
                for (int tileLeft = 0; tileLeft <= cols - w; tileLeft += 3)
                for (int tileTop = 0; tileTop <= rows - h; tileTop += 3)
                {
                    int tileRight = tileLeft + w - 1, tileBottom = tileTop + h - 1;

                    // Another tile has ALREADY parked icons in a handful of cells just
                    // outside this tile's own footprint — plausible if it sits nearby.
                    var reserved = new HashSet<(int, int)>();
                    for (int dc = -2; dc <= w + 1; dc++)
                    {
                        int rc = tileLeft + dc;
                        int rr = tileBottom + 1; // just below the tile, a common "pushed to" spot
                        if (rc >= 0 && rc < cols && rr < rows) reserved.Add(Cell(rc, rr));
                    }

                    var before = new Dictionary<int, (int col, int row)>(icons);
                    var localCells = before.ToDictionary(kv => kv.Key, kv => (DesktopShell.IconGrid.LocalCol(kv.Value.col), kv.Value.row));

                    var placements = DesktopShell.ComputeColumnPush(localCells, tileLeft, tileRight, tileTop, tileBottom, cols - 1, rows - 1);
                    var moves = new List<(int idx, (int col, int row) to, int stage)>();
                    foreach (var pl in placements)
                    {
                        var to = Cell(pl.to.col, pl.to.row);
                        if (before[pl.idx] != to) moves.Add((pl.idx, to, pl.stage));
                    }

                    if (placements.Overflow.Count > 0)
                    {
                        // Mirror MakeSpace's own overflow handling isn't reproduced here
                        // (private FindFreeCell) — skip cases that overflow; the sweep
                        // below still covers the vast majority of positions.
                        continue;
                    }

                    var afterAvoidance = reserved.Count > 0
                        ? DesktopShell.AvoidReservedCells(moves, before, grid, reserved, bounds, Rect.Empty)
                        : moves;

                    // 1. No result lands on a reserved cell.
                    foreach (var m in afterAvoidance)
                        Assert.False(reserved.Contains(m.to),
                            $"seed={seed} shape={w}x{h} tile=({tileLeft},{tileTop}): icon {m.idx} landed on reserved cell {m.to}");

                    // 2. No two results land on the same cell (self-collision).
                    var destinations = afterAvoidance.Select(m => m.to).ToList();
                    var dupes = destinations.GroupBy(d => d).Where(g => g.Count() > 1).ToList();
                    Assert.True(dupes.Count == 0,
                        $"seed={seed} shape={w}x{h} tile=({tileLeft},{tileTop}): duplicate destination(s) " +
                        string.Join(", ", dupes.Select(g => $"{g.Key} x{g.Count()}")));

                    // 3. No result lands on a cell some OTHER, un-moved icon still occupies.
                    var movedIdx = new HashSet<int>(afterAvoidance.Select(m => m.idx));
                    var stillThere = before.Where(kv => !movedIdx.Contains(kv.Key))
                        .Select(kv => kv.Value).ToHashSet();
                    foreach (var m in afterAvoidance)
                        Assert.False(stillThere.Contains(m.to),
                            $"seed={seed} shape={w}x{h} tile=({tileLeft},{tileTop}): icon {m.idx} landed on cell {m.to} still occupied by an unmoved icon");
                }
            }
        }
    }
}

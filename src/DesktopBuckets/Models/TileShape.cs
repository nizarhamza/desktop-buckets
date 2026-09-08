using System.Linq;

namespace DesktopBuckets.Models
{
    /// <summary>
    /// The fixed set of grid shapes (Rows x Columns) a bucket tile can ever render as.
    /// Deliberately a short, hand-picked list rather than a formula on item count —
    /// the old cols=ceil(sqrt(n)), rows=ceil(n/cols) approach could produce an odd
    /// squarish shape with a visible empty gap for a count a clean single row would
    /// have fit better (3 files as a 2x2 with one empty cell, instead of a tidy 1x3).
    /// Sizing responds to how much content there actually is — which of these 5 shapes
    /// gets used — but the SHAPE ITSELF is always one of exactly these five; nothing
    /// else is ever produced.
    /// </summary>
    public static class TileShape
    {
        /// <summary>Ordered smallest capacity first; capacity = Rows * Columns.</summary>
        public static readonly (int Rows, int Cols)[] All =
        {
            (1, 2), (1, 3), (2, 2), (2, 3), (3, 3),
        };

        /// <summary>The smallest shape that can hold at least <paramref name="count"/>
        /// items. A count of zero or one still gets the smallest shape (1x2) — there's
        /// no 1x1 option, so even a single-file bucket has room to grow into a second
        /// slot without immediately needing a reshape. A count above the largest shape's
        /// capacity (9) is capped at 3x3 — extra files still exist in the folder, just
        /// not all shown at once (see FileRankingService / OverflowCount).</summary>
        public static (int Rows, int Cols) For(int count)
        {
            foreach (var shape in All)
                if (shape.Rows * shape.Cols >= count) return shape;
            return All[^1];
        }

        /// <summary>Capacity (Rows * Columns) of the shape <see cref="For"/> would pick
        /// for this count — i.e. the nearest one of {2, 3, 4, 6, 9} at or above it.</summary>
        public static int CapacityFor(int count) => Capacity(For(count));

        public static int Capacity((int Rows, int Cols) shape) => shape.Rows * shape.Cols;

        /// <summary>True if a stored SlotCount already exactly names one of the five
        /// shapes' capacities (2, 3, 4, 6, 9) rather than some other, non-canonical
        /// value left over from before this shape set existed.</summary>
        public static bool IsCanonicalCapacity(int count) => All.Any(s => Capacity(s) == count);
    }
}

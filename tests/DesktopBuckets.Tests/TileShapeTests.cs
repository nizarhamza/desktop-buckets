using DesktopBuckets.Models;
using Xunit;

namespace DesktopBuckets.Tests
{
    /// <summary>
    /// Exercises TileShape directly. Bucket sizing was reworked (user request) to pick
    /// from exactly 5 hand-picked Rows x Columns shapes — 1x2, 1x3, 2x2, 2x3, 3x3 — never
    /// any other combination, replacing a sqrt-based formula on raw item count that could
    /// produce an odd shape with an empty gap (3 files as a 2x2 with a hole instead of a
    /// clean 1x3 row).
    /// </summary>
    public class TileShapeTests
    {
        [Fact]
        public void ExactlyFiveShapesExist()
        {
            Assert.Equal(5, TileShape.All.Length);
        }

        [Theory]
        [InlineData(1, 2)]
        [InlineData(1, 3)]
        [InlineData(2, 2)]
        [InlineData(2, 3)]
        [InlineData(3, 3)]
        public void OnlyTheseFiveShapesAreEverProduced(int rows, int cols)
        {
            Assert.Contains((rows, cols), TileShape.All);
        }

        [Fact]
        public void NoShapeIsOneByOne()
        {
            Assert.DoesNotContain((1, 1), TileShape.All);
        }

        [Theory]
        [InlineData(0, 1, 2)]   // empty bucket still needs a shape to render its placeholder in
        [InlineData(1, 1, 2)]
        [InlineData(2, 1, 2)]
        [InlineData(3, 1, 3)]   // 3 items: a clean single row, not a 2x2 with a gap
        [InlineData(4, 2, 2)]
        [InlineData(5, 2, 3)]
        [InlineData(6, 2, 3)]
        [InlineData(7, 3, 3)]
        [InlineData(8, 3, 3)]
        [InlineData(9, 3, 3)]
        [InlineData(50, 3, 3)]  // way more files than any shape holds: capped at 3x3
        public void ForPicksTheSmallestShapeThatFitsTheCount(int count, int expectedRows, int expectedCols)
        {
            var shape = TileShape.For(count);
            Assert.Equal((expectedRows, expectedCols), shape);
        }

        [Theory]
        [InlineData(0, 2)]
        [InlineData(2, 2)]
        [InlineData(3, 3)]
        [InlineData(4, 4)]
        [InlineData(5, 6)]
        [InlineData(6, 6)]
        [InlineData(9, 9)]
        [InlineData(20, 9)]
        public void CapacityForMatchesTheChosenShapesCellCount(int count, int expectedCapacity)
        {
            Assert.Equal(expectedCapacity, TileShape.CapacityFor(count));
        }

        [Theory]
        [InlineData(2, true)]
        [InlineData(3, true)]
        [InlineData(4, true)]
        [InlineData(6, true)]
        [InlineData(9, true)]
        [InlineData(1, false)]
        [InlineData(5, false)]
        [InlineData(7, false)]
        [InlineData(8, false)]
        [InlineData(0, false)]
        public void IsCanonicalCapacityIdentifiesExactShapeCapacitiesOnly(int count, bool expected)
        {
            Assert.Equal(expected, TileShape.IsCanonicalCapacity(count));
        }

        [Fact]
        public void ShapesAreOrderedByAscendingCapacity()
        {
            int last = 0;
            foreach (var shape in TileShape.All)
            {
                int cap = TileShape.Capacity(shape);
                Assert.True(cap > last, "TileShape.All must be sorted by ascending capacity");
                last = cap;
            }
        }
    }
}

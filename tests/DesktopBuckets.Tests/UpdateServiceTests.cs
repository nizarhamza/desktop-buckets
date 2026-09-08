using System;
using DesktopBuckets.Services;
using Xunit;

namespace DesktopBuckets.Tests
{
    public class UpdateServiceTests
    {
        [Theory]
        [InlineData("0.1.412", "0.1.412.0")]
        [InlineData("v0.2.1", "0.2.1.0")]
        [InlineData("V1.2", "1.2.0.0")]
        [InlineData("1.2.3.4", "1.2.3.4")]
        [InlineData("1.2.3-beta", "1.2.3.0")]
        [InlineData("1.2.3+build.7", "1.2.3.0")]
        [InlineData("  v3.0.0  ", "3.0.0.0")]
        public void ParseVersionNormalisesToFourParts(string input, string expected)
        {
            Assert.Equal(Version.Parse(expected), UpdateService.ParseVersion(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("nightly")]
        [InlineData("v")]
        [InlineData("1")]
        public void ParseVersionRejectsGarbage(string? input)
        {
            Assert.Null(UpdateService.ParseVersion(input));
        }

        [Fact]
        public void FormatVersionDropsZeroRevision()
        {
            Assert.Equal("0.2.1", UpdateService.FormatVersion(new Version(0, 2, 1, 0)));
            Assert.Equal("0.2.1.5", UpdateService.FormatVersion(new Version(0, 2, 1, 5)));
        }

        [Fact]
        public void NightlyIsNewerThanStableByNumberOnly()
        {
            // Documents M11 from the review: nightly 0.1.412 compares above stable 0.2.1's
            // predecessor 0.1.x, but a stable v0.2.x compares above every 0.1.<run>.
            var nightly = UpdateService.ParseVersion("0.1.412")!;
            var stable = UpdateService.ParseVersion("v0.2.1")!;
            Assert.True(stable > nightly);
        }
    }
}

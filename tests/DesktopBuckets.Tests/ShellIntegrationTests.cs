using DesktopBuckets.Services;
using Xunit;

namespace DesktopBuckets.Tests
{
    public class ShellIntegrationTests
    {
        [Theory]
        // Real value from `Get-AppxPackage DesktopBuckets.ShellExt` on the dev machine.
        [InlineData("CN=DesktopBuckets Dev", "ttbfq57rshjxa")]
        // Microsoft's own publisher, family "Microsoft.WindowsCalculator_8wekyb3d8bbwe".
        [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", "8wekyb3d8bbwe")]
        public void PublisherIdHashMatchesWindows(string publisher, string expected)
        {
            Assert.Equal(expected, ShellIntegration.PublisherIdHash(publisher));
        }
    }
}

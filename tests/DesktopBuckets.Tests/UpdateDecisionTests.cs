using System;
using System.IO;
using DesktopBuckets.Interop;
using DesktopBuckets.Services;
using Xunit;

namespace DesktopBuckets.Tests
{
    public class UpdateDecisionTests
    {
        private static readonly Version Nightly412 = new(0, 1, 412, 0);
        private static readonly Version Stable021 = new(0, 2, 1, 0);
        private static readonly Version Stable010 = new(0, 1, 0, 0);

        [Fact]
        public void NewerIsOffered()
        {
            Assert.Equal(UpdateService.Decision.Offer, UpdateService.Decide(Stable010, Stable021, false));
            Assert.Equal(UpdateService.Decision.Offer, UpdateService.Decide(Stable010, Stable021, true));
        }

        [Fact]
        public void SameIsUpToDate()
        {
            Assert.Equal(UpdateService.Decision.UpToDate, UpdateService.Decide(Stable021, Stable021, false));
            Assert.Equal(UpdateService.Decision.UpToDate, UpdateService.Decide(Stable021, Stable021, true));
        }

        [Fact]
        public void OlderIsIgnoredUnlessSwitchingChannel()
        {
            // Nightly 0.1.412 -> stable 0.1.0 (M11 from the review).
            Assert.Equal(UpdateService.Decision.UpToDate, UpdateService.Decide(Nightly412, Stable010, false));
            Assert.Equal(UpdateService.Decision.OfferDowngrade, UpdateService.Decide(Nightly412, Stable010, true));
        }

        [Theory]
        [InlineData(null, "nightly")]
        [InlineData("", "nightly")]
        [InlineData("Stable", "stable")]
        [InlineData("  stable ", "stable")]
        [InlineData("beta", "nightly")]
        public void ChannelNormalises(string? input, string expected)
        {
            Assert.Equal(expected, UpdateService.NormalizeChannel(input));
        }

        // ---- installer verification ----------------------------------

        [Fact]
        public void UnsignedFileIsRefused()
        {
            using var tmp = new TempDir();
            var p = tmp.File("setup.exe", new string('x', 4096));
            var reason = UpdateService.VerifyInstaller(p);
            Assert.NotNull(reason);
            Assert.Contains("not signed", reason);
        }

        [Fact]
        public void WindowsSignedBinaryIsIntactButNotOurSigner()
        {
            var kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
            Assert.Equal(Authenticode.Status.Valid, Authenticode.Verify(kernel32, out _));
            Assert.NotNull(Authenticode.SignerThumbprint(kernel32));

            var reason = UpdateService.VerifyInstaller(kernel32);
            Assert.NotNull(reason);
            Assert.Contains("unexpected certificate", reason);
        }

        [Fact]
        public void TamperedSignedBinaryIsRefused()
        {
            using var tmp = new TempDir();
            var copy = Path.Combine(tmp.Path, "k.dll");
            File.Copy(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), copy);
            // flip a byte in the middle of the image (well inside the signed range)
            using (var fs = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite))
            {
                fs.Position = fs.Length / 2;
                int b = fs.ReadByte();
                fs.Position = fs.Length / 2;
                fs.WriteByte((byte)(b ^ 0xFF));
            }
            Assert.Equal(Authenticode.Status.Tampered, Authenticode.Verify(copy, out _));
            Assert.Contains("does not match", UpdateService.VerifyInstaller(copy));
        }
    }
}

using DesktopBuckets.Models;
using Xunit;

namespace DesktopBuckets.Tests
{
    public class UpdateConfigTests
    {
        [Fact]
        public void PlaintextTokenIsProtectedAndStillResolves()
        {
            var c = new UpdateConfig { Token = "  ghp_secret123  " };
            Assert.False(c.TokenIsProtected);

            Assert.True(c.ProtectToken());
            Assert.True(c.TokenIsProtected);
            Assert.StartsWith("dpapi:", c.Token);
            Assert.DoesNotContain("ghp_secret123", c.Token);
            Assert.Equal("ghp_secret123", c.ResolveToken());

            Assert.False(c.ProtectToken()); // idempotent
        }

        [Fact]
        public void EmptyTokenIsLeftAlone()
        {
            var c = new UpdateConfig { Token = null };
            Assert.False(c.ProtectToken());
            Assert.Null(c.ResolveToken());
            c.Token = "   ";
            Assert.False(c.ProtectToken());
            Assert.Null(c.ResolveToken());
        }

        [Fact]
        public void UndecryptableProtectedTokenResolvesToNull()
        {
            var c = new UpdateConfig { Token = "dpapi:AAAA" };
            Assert.Null(c.ResolveToken());
        }

        [Fact]
        public void DefaultRepoIsRecognisedCaseInsensitively()
        {
            Assert.True(new UpdateConfig().IsDefaultRepo);
            Assert.True(new UpdateConfig { Repo = " NizarHamza/Desktop-Buckets " }.IsDefaultRepo);
            Assert.False(new UpdateConfig { Repo = "someone/else" }.IsDefaultRepo);
        }
    }
}

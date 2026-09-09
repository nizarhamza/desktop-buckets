using System.Windows.Media;
using DesktopBuckets.Models;
using DesktopBuckets.Services;
using Xunit;

namespace DesktopBuckets.Tests
{
    public class AppearanceConfigTests
    {
        [Fact]
        public void Defaults_are_in_range_and_sensible()
        {
            var c = new AppearanceConfig();
            Assert.Equal(AppTheme.System, c.Theme);
            Assert.InRange(c.TileTransparencyPercent, 0, 100);
            Assert.InRange(c.TileCornerRadius, 0, 24);
            Assert.True(c.BlurBehindTiles);
            Assert.True(c.ShowTileBorder);
            Assert.True(c.UseSystemAccent);
        }

        [Theory]
        [InlineData(-40, 0)]
        [InlineData(0, 0)]
        [InlineData(57, 57)]
        [InlineData(100, 100)]
        [InlineData(250, 100)]
        public void Normalized_clamps_transparency(int input, int expected)
        {
            var c = new AppearanceConfig { TileTransparencyPercent = input }.Normalized();
            Assert.Equal(expected, c.TileTransparencyPercent);
        }

        [Theory]
        [InlineData(-3, 0)]
        [InlineData(12, 12)]
        [InlineData(24, 24)]
        [InlineData(99, 24)]
        public void Normalized_clamps_corner_radius(int input, int expected)
        {
            var c = new AppearanceConfig { TileCornerRadius = input }.Normalized();
            Assert.Equal(expected, c.TileCornerRadius);
        }

        [Fact]
        public void Normalized_falls_back_on_a_junk_accent_string()
        {
            var c = new AppearanceConfig { AccentColor = "not-a-colour" }.Normalized();
            Assert.Equal("#4C8BF5", c.AccentColor);
        }

        [Fact]
        public void Normalized_keeps_and_upper_cases_a_valid_accent()
        {
            var c = new AppearanceConfig { AccentColor = "#4c8bf5" }.Normalized();
            Assert.Equal("#4C8BF5", c.AccentColor);
        }

        [Fact]
        public void Normalized_repairs_an_undefined_theme_value()
        {
            var c = new AppearanceConfig { Theme = (AppTheme)99 }.Normalized();
            Assert.Equal(AppTheme.System, c.Theme);
        }

        [Theory]
        [InlineData(98, 2)]
        [InlineData(0, 100)]
        [InlineData(100, 0)]
        [InlineData(30, 70)]
        public void TileFillAlphaPercent_is_the_inverse_of_transparency(int transparency, int alpha)
        {
            var c = new AppearanceConfig { TileTransparencyPercent = transparency };
            Assert.Equal(alpha, c.TileFillAlphaPercent);
        }

        [Theory]
        [InlineData("#4C8BF5", "#4C8BF5")]
        [InlineData("4c8bf5", "#4C8BF5")]
        [InlineData("#ff4c8bf5", "#FF4C8BF5")]
        [InlineData("  #AbCdEf  ", "#ABCDEF")]
        public void NormalizeHex_accepts_colour_literals(string input, string expected)
        {
            Assert.Equal(expected, AppearanceConfig.NormalizeHex(input));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("blue")]
        [InlineData("#12345")]
        [InlineData("#1234567")]
        [InlineData("#GG0000")]
        [InlineData(null)]
        public void NormalizeHex_rejects_everything_else(string? input)
        {
            Assert.Null(AppearanceConfig.NormalizeHex(input));
        }

        [Theory]
        [InlineData(AppTheme.System, true, AppTheme.Light)]
        [InlineData(AppTheme.System, false, AppTheme.Dark)]
        [InlineData(AppTheme.Light, false, AppTheme.Light)]
        [InlineData(AppTheme.Dark, true, AppTheme.Dark)]
        public void Resolve_maps_choice_and_windows_flag_to_an_effective_theme(
            AppTheme choice, bool windowsAppsUseLight, AppTheme expected)
        {
            Assert.Equal(expected, AppearanceService.Resolve(choice, windowsAppsUseLight));
        }

        [Theory]
        [InlineData("#4C8BF5", 0xFF, 0x4C, 0x8B, 0xF5)]
        [InlineData("4c8bf5", 0xFF, 0x4C, 0x8B, 0xF5)]
        [InlineData("#80AABBCC", 0x80, 0xAA, 0xBB, 0xCC)]
        public void TryParseColor_reads_rgb_and_argb(string hex, int a, int r, int g, int b)
        {
            Assert.True(AppearanceService.TryParseColor(hex, out var c));
            Assert.Equal(Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b), c);
        }

        [Theory]
        [InlineData("periwinkle")]
        [InlineData("#12345")]
        [InlineData("#8022446688")]
        [InlineData(null)]
        public void TryParseColor_rejects_junk(string? hex)
        {
            Assert.False(AppearanceService.TryParseColor(hex, out _));
        }

        [Fact]
        public void Shade_darkens_toward_black()
        {
            Assert.Equal(Colors.White, AppearanceService.Shade(Colors.White, 0));
            var half = AppearanceService.Shade(Colors.White, 0.5);
            Assert.InRange(half.R, (byte)126, (byte)128);
            Assert.Equal(Color.FromRgb(0, 0, 0), AppearanceService.Shade(Colors.White, 1));
        }

        [Fact]
        public void IdealForeground_picks_readable_contrast()
        {
            Assert.Equal(Colors.White, AppearanceService.IdealForeground(Color.FromRgb(0x10, 0x10, 0x10)));
            var onLight = AppearanceService.IdealForeground(Color.FromRgb(0xF0, 0xF0, 0xF0));
            Assert.True(onLight.R < 0x40 && onLight.G < 0x40 && onLight.B < 0x40);
        }
    }
}

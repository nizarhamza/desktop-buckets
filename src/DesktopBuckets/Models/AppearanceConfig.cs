using System;
using System.Text.Json.Serialization;

namespace DesktopBuckets.Models
{
    /// <summary>Light / dark selection. <see cref="System"/> follows the Windows
    /// "Choose your default app mode" setting and tracks it live.</summary>
    public enum AppTheme
    {
        System = 0,
        Light = 1,
        Dark = 2,
    }

    /// <summary>
    /// App-wide look &amp; feel, persisted at
    /// <c>%USERPROFILE%\Desktop Buckets\.app\appearance.json</c>. Unlike
    /// <see cref="BucketConfig"/> (per bucket) this is one shared file — every tile and
    /// every window reads the same values, and the Settings &gt; Appearance page is the
    /// only thing that writes them.
    /// </summary>
    public sealed class AppearanceConfig
    {
        /// <summary>Window / menu / tile colour scheme.</summary>
        public AppTheme Theme { get; set; } = AppTheme.System;

        /// <summary>How see-through a tile's glass background is, 0–100 %.
        /// 100 = the fill is fully invisible (pure blurred wallpaper); 0 = solid.
        /// Stored as the <i>transparency</i> the user sees on the slider; the tile
        /// converts it to a fill alpha of <c>(100 - value) %</c>.</summary>
        public int TileTransparencyPercent { get; set; } = 98;

        /// <summary>Frost (blur) whatever shows through the tile glass. Off = a plain
        /// translucent panel, which is cheaper and avoids the OS's occasional
        /// washed-out acrylic on older Win11 builds.</summary>
        public bool BlurBehindTiles { get; set; } = true;

        /// <summary>Tile corner radius in device-independent pixels, 0–24.</summary>
        public int TileCornerRadius { get; set; } = 12;

        /// <summary>Draw the 1 px hairline outline around a tile.</summary>
        public bool ShowTileBorder { get; set; } = true;

        /// <summary>Take the accent colour from Windows' personalisation setting and
        /// follow it live. When true, <see cref="AccentColor"/> is ignored.</summary>
        public bool UseSystemAccent { get; set; } = true;

        /// <summary>Accent used for toggles, selection and the pin dot when
        /// <see cref="UseSystemAccent"/> is false. <c>#RRGGBB</c> (or <c>#AARRGGBB</c>).</summary>
        public string AccentColor { get; set; } = "#4C8BF5";

        [JsonIgnore]
        public int TileFillAlphaPercent => 100 - Clamp(TileTransparencyPercent, 0, 100);

        /// <summary>A copy with every numeric field forced into its valid range and the
        /// accent string sanitised. Callers persist and use this, never the raw
        /// deserialised object — a hand-edited file can hold anything.</summary>
        public AppearanceConfig Normalized() => new()
        {
            Theme = Enum.IsDefined(typeof(AppTheme), Theme) ? Theme : AppTheme.System,
            TileTransparencyPercent = Clamp(TileTransparencyPercent, 0, 100),
            BlurBehindTiles = BlurBehindTiles,
            TileCornerRadius = Clamp(TileCornerRadius, 0, 24),
            ShowTileBorder = ShowTileBorder,
            UseSystemAccent = UseSystemAccent,
            AccentColor = NormalizeHex(AccentColor) ?? "#4C8BF5",
        };

        /// <summary>Returns <paramref name="hex"/> as <c>#RRGGBB</c>/<c>#AARRGGBB</c>
        /// (upper-case, with the leading #), or null if it isn't a colour literal.</summary>
        public static string? NormalizeHex(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            var s = hex.Trim().TrimStart('#');
            if (s.Length is not (6 or 8)) return null;
            foreach (var c in s)
                if (!Uri.IsHexDigit(c)) return null;
            return "#" + s.ToUpperInvariant();
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    }
}

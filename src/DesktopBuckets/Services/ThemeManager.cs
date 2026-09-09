using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using DesktopBuckets.Models;

namespace DesktopBuckets.Services
{
    /// <summary>
    /// Swaps the app's palette resource dictionary (light ⇄ dark) and re-derives the
    /// accent brushes whenever <see cref="AppearanceService"/> says the effective theme
    /// or accent changed. Every themed brush is referenced with <c>DynamicResource</c>,
    /// so open windows and menus repaint on their own once the dictionary is replaced.
    /// </summary>
    public static class ThemeManager
    {
        private const string DarkPalette = "pack://application:,,,/Themes/Palette.Dark.xaml";
        private const string LightPalette = "pack://application:,,,/Themes/Palette.Light.xaml";

        /// <summary>The theme currently applied (never <see cref="AppTheme.System"/>).</summary>
        public static AppTheme Current { get; private set; } = AppTheme.Dark;

        public static void Initialize()
        {
            Apply();
            AppearanceService.Changed -= Apply;
            AppearanceService.Changed += Apply;
        }

        private static void Apply() => Apply(AppearanceService.ResolvedTheme, AppearanceService.ResolvedAccent);

        public static void Apply(AppTheme theme, Color accent)
        {
            var app = Application.Current;
            if (app is null) return;

            Current = theme;
            var wanted = theme == AppTheme.Light ? LightPalette : DarkPalette;

            var merged = app.Resources.MergedDictionaries;
            bool alreadyRight = merged.Count > 0 && PaletteSourceMatches(merged[0], wanted);
            if (!alreadyRight)
            {
                var dict = new ResourceDictionary { Source = new Uri(wanted, UriKind.Absolute) };
                if (merged.Count == 0) merged.Add(dict);
                else merged[0] = dict;
            }

            // Accent isn't in the palette files — it comes from the config / Windows and
            // is asserted here at the top level, where DynamicResource looks first.
            var r = app.Resources;
            r["App.Accent"] = Frozen(accent);
            r["App.AccentHover"] = Frozen(AppearanceService.Shade(accent, 0.12));
            r["App.AccentPressed"] = Frozen(AppearanceService.Shade(accent, 0.24));
            r["App.AccentText"] = Frozen(AppearanceService.IdealForeground(accent));
            r["App.AccentColor"] = accent;
        }

        private static bool PaletteSourceMatches(ResourceDictionary d, string wanted)
        {
            var src = d.Source?.ToString();
            if (src is null) return false;
            // "/Themes/Palette.Dark.xaml" vs the full pack URI — compare the tail.
            return wanted.EndsWith(src.TrimStart('/'), StringComparison.OrdinalIgnoreCase)
                || src.EndsWith(wanted.Split('/').Last(), StringComparison.OrdinalIgnoreCase);
        }

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}

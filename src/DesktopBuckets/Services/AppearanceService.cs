using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using DesktopBuckets.Models;

namespace DesktopBuckets.Services
{
    /// <summary>
    /// Single source of truth for the app's look: the persisted <see cref="AppearanceConfig"/>,
    /// plus the two things that depend on Windows itself — the resolved light/dark theme
    /// (when the user picked "System") and the accent colour. Raises <see cref="Changed"/>
    /// whenever any of that moves, whether from the Settings page or from Windows'
    /// personalisation settings changing under us.
    /// </summary>
    public static class AppearanceService
    {
        private static readonly object Gate = new();
        private static AppearanceConfig _current = new AppearanceConfig().Normalized();
        private static Dispatcher? _dispatcher;
        private static bool _initialised;

        // Last resolved values, so a Windows personalisation change only fires Changed
        // when something we actually render from is different.
        private static AppTheme _lastResolvedTheme = AppTheme.Dark;
        private static Color _lastResolvedAccent = Color.FromRgb(0x4C, 0x8B, 0xF5);

        /// <summary>Fired (on the UI dispatcher) after the config or an inherited Windows
        /// setting changes. Windows, tiles and the theme manager re-read and re-apply.</summary>
        public static event Action? Changed;

        public static string ConfigPath => Path.Combine(BucketStore.AppDataDir, "appearance.json");

        /// <summary>The current, always-normalised config. Never null.</summary>
        public static AppearanceConfig Current
        {
            get { lock (Gate) return _current; }
        }

        /// <summary>Load the file and start listening for Windows theme/accent changes.
        /// Safe to call once, early in startup.</summary>
        public static void Initialize(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            Load();
            _lastResolvedTheme = ResolvedTheme;
            _lastResolvedAccent = ResolvedAccent;

            if (!_initialised)
            {
                _initialised = true;
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            }
        }

        public static void Load()
        {
            var raw = JsonUtil.Read<AppearanceConfig>(ConfigPath) ?? new AppearanceConfig();
            lock (Gate) _current = raw.Normalized();
        }

        /// <summary>Mutate the config and notify everyone. The lambda gets a working
        /// copy; whatever it leaves is normalised before it is saved or seen.</summary>
        /// <param name="persist">Write the file now. Pass false for the rapid stream of
        /// changes a slider drag produces and call <see cref="Persist"/> once it settles,
        /// so a drag doesn't rewrite <c>appearance.json</c> dozens of times.</param>
        public static void Update(Action<AppearanceConfig> mutate, bool persist = true)
        {
            if (mutate is null) throw new ArgumentNullException(nameof(mutate));

            AppearanceConfig next;
            lock (Gate)
            {
                next = Clone(_current);
                mutate(next);
                next = next.Normalized();
                _current = next;
            }

            if (persist) JsonUtil.Write(ConfigPath, next);
            _lastResolvedTheme = ResolvedTheme;
            _lastResolvedAccent = ResolvedAccent;
            RaiseChanged();
        }

        /// <summary>Write the current config to disk. Pairs with
        /// <c>Update(..., persist: false)</c>.</summary>
        public static void Persist()
        {
            AppearanceConfig snap;
            lock (Gate) snap = _current;
            JsonUtil.Write(ConfigPath, snap);
        }

        // ---- resolved-from-Windows values -------------------------------

        /// <summary>The effective theme — <see cref="AppTheme.Light"/> or
        /// <see cref="AppTheme.Dark"/>, never <see cref="AppTheme.System"/>.</summary>
        public static AppTheme ResolvedTheme => Resolve(Current.Theme, WindowsAppsUseLightTheme());

        /// <summary>Pure resolver: what <paramref name="choice"/> means given Windows'
        /// current "apps use light theme" flag. Split out so it is unit-testable without
        /// touching the registry.</summary>
        public static AppTheme Resolve(AppTheme choice, bool windowsAppsUseLight) => choice switch
        {
            AppTheme.Light => AppTheme.Light,
            AppTheme.Dark => AppTheme.Dark,
            _ => windowsAppsUseLight ? AppTheme.Light : AppTheme.Dark,
        };

        /// <summary>The accent colour to paint toggles / selection / the pin dot with.</summary>
        public static Color ResolvedAccent
        {
            get
            {
                var cfg = Current;
                if (!cfg.UseSystemAccent && TryParseColor(cfg.AccentColor, out var c))
                    return c;
                return WindowsAccentColor() ?? Color.FromRgb(0x4C, 0x8B, 0xF5);
            }
        }

        /// <summary>#RRGGBB / #AARRGGBB → <see cref="Color"/>. False for anything else.</summary>
        public static bool TryParseColor(string? hex, out Color color)
        {
            color = Colors.Transparent;
            var s = AppearanceConfig.NormalizeHex(hex);
            if (s is null) return false;
            s = s.TrimStart('#');
            try
            {
                byte a = 0xFF, r, g, b;
                if (s.Length == 8)
                {
                    a = Convert.ToByte(s.Substring(0, 2), 16);
                    r = Convert.ToByte(s.Substring(2, 2), 16);
                    g = Convert.ToByte(s.Substring(4, 2), 16);
                    b = Convert.ToByte(s.Substring(6, 2), 16);
                }
                else
                {
                    r = Convert.ToByte(s.Substring(0, 2), 16);
                    g = Convert.ToByte(s.Substring(2, 2), 16);
                    b = Convert.ToByte(s.Substring(4, 2), 16);
                }
                color = Color.FromArgb(a, r, g, b);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>HKCU personalisation flag. Missing key ⇒ Windows default ⇒ light.</summary>
        public static bool WindowsAppsUseLightTheme()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is not int v || v != 0;
            }
            catch (Exception) { return true; }
        }

        private static Color? WindowsAccentColor()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (k?.GetValue("AccentColor") is int abgr)
                {
                    // stored 0xAABBGGRR
                    byte r = (byte)(abgr & 0xFF);
                    byte g = (byte)((abgr >> 8) & 0xFF);
                    byte b = (byte)((abgr >> 16) & 0xFF);
                    return Color.FromRgb(r, g, b);
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>Blend <paramref name="c"/> toward black by <paramref name="amount"/>
        /// (0–1). Used for the pressed/hover accent shades.</summary>
        public static Color Shade(Color c, double amount)
        {
            amount = amount < 0 ? 0 : amount > 1 ? 1 : amount;
            return Color.FromRgb(
                (byte)(c.R * (1 - amount)),
                (byte)(c.G * (1 - amount)),
                (byte)(c.B * (1 - amount)));
        }

        /// <summary>Black or white, whichever reads better on <paramref name="bg"/>.</summary>
        public static Color IdealForeground(Color bg)
        {
            double luma = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
            return luma > 0.6 ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White;
        }

        // ---- internals -------------------------------------------------

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color
                or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Window))
                return;

            var theme = ResolvedTheme;
            var accent = ResolvedAccent;
            if (theme == _lastResolvedTheme && accent == _lastResolvedAccent) return;

            _lastResolvedTheme = theme;
            _lastResolvedAccent = accent;
            RaiseChanged();
        }

        private static void RaiseChanged()
        {
            var d = _dispatcher;
            if (d is null || d.CheckAccess()) Changed?.Invoke();
            else d.BeginInvoke(new Action(() => Changed?.Invoke()));
        }

        private static AppearanceConfig Clone(AppearanceConfig s) => new()
        {
            Theme = s.Theme,
            TileTransparencyPercent = s.TileTransparencyPercent,
            BlurBehindTiles = s.BlurBehindTiles,
            TileCornerRadius = s.TileCornerRadius,
            ShowTileBorder = s.ShowTileBorder,
            UseSystemAccent = s.UseSystemAccent,
            AccentColor = s.AccentColor,
        };
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopBuckets.Interop;
using DesktopBuckets.Models;
using DesktopBuckets.Services;

namespace DesktopBuckets.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly UpdateService _update;
        private readonly IBucketHost _host;
        private bool _loading;
        private readonly DispatcherTimer _persistTimer;

        // name -> #RRGGBB, offered when "Use my Windows accent colour" is off.
        private static readonly (string Name, string Hex)[] AccentPresets =
        {
            ("Blue", "#4C8BF5"), ("Windows blue", "#0078D4"), ("Purple", "#8E5BD9"),
            ("Pink", "#D8437E"), ("Red", "#C0392B"), ("Orange", "#E67E22"),
            ("Teal", "#12A19A"), ("Green", "#2E9E4F"),
        };

        public SettingsWindow(UpdateService update, IBucketHost host)
        {
            _loading = true;
            _update = update;
            _host = host;
            InitializeComponent();

            WindowChromeHelper.Attach(this);

            _persistTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(400),
            };
            _persistTimer.Tick += (_, _) => { _persistTimer.Stop(); AppearanceService.Persist(); };

            _update.UpToDateOrError += OnUpdateStatus;
            _update.UpdateAvailable += OnUpdateAvailable;
            AppearanceService.Changed += OnAppearanceChanged;

            Closed += (_, _) =>
            {
                _update.UpToDateOrError -= OnUpdateStatus;
                _update.UpdateAvailable -= OnUpdateAvailable;
                AppearanceService.Changed -= OnAppearanceChanged;
                if (_persistTimer.IsEnabled) { _persistTimer.Stop(); AppearanceService.Persist(); }
            };

            BuildSwatches();
            LoadFromState();
            UpdatePageVisibility();
        }

        // ---- load -----------------------------------------------------

        private void LoadFromState()
        {
            _loading = true;

            var v = "version " + UpdateService.FormatVersion(_update.CurrentVersion);
            VersionText.Text = v;
            AboutVersionText.Text = v;

            var c = _update.Config;
            AutoUpdateCheck.IsChecked = c.Enabled;
            StartupCheck.IsChecked = c.CheckOnStartup;
            StartupCheck.IsEnabled = c.Enabled;

            SelectByTag(ChannelCombo,
                string.Equals(c.Channel, "stable", StringComparison.OrdinalIgnoreCase) ? "stable" : "nightly");

            int hrs = (int)Math.Round(c.CheckIntervalHours);
            SelectByTag(IntervalCombo, hrs <= 1 ? "1" : hrs <= 6 ? "6" : "24");
            IntervalCombo.IsEnabled = ChannelCombo.IsEnabled = c.Enabled;

            RunAtSignInCheck.IsChecked = StartupRegistration.IsEnabled;
            ShellMenuCheck.IsChecked = _host.ShellIntegrationEnabled;

            DataDirText.Text = BucketStore.AppDataDir;
            DataDirText.ToolTip = BucketStore.AppDataDir;

            LoadAppearanceState();

            _loading = false;
        }

        private void LoadAppearanceState()
        {
            bool prev = _loading;
            _loading = true;

            var a = AppearanceService.Current;

            ThemeSystem.IsChecked = a.Theme == AppTheme.System;
            ThemeLight.IsChecked = a.Theme == AppTheme.Light;
            ThemeDark.IsChecked = a.Theme == AppTheme.Dark;

            TransparencySlider.Value = a.TileTransparencyPercent;
            TransparencyValue.Text = a.TileTransparencyPercent + "%";
            CornerSlider.Value = a.TileCornerRadius;
            CornerValue.Text = a.TileCornerRadius + " px";
            BlurCheck.IsChecked = a.BlurBehindTiles;
            BorderCheck.IsChecked = a.ShowTileBorder;

            AccentSystemCheck.IsChecked = a.UseSystemAccent;
            SwatchPanel.IsEnabled = !a.UseSystemAccent;
            var wantHex = AppearanceConfig.NormalizeHex(a.AccentColor);
            foreach (var rb in SwatchPanel.Children.OfType<RadioButton>())
                rb.IsChecked = !a.UseSystemAccent
                    && string.Equals((string?)rb.Tag, wantHex, StringComparison.OrdinalIgnoreCase);

            UpdateTilePreview();

            _loading = prev;
        }

        private void OnAppearanceChanged() => LoadAppearanceState();

        private void BuildSwatches()
        {
            var style = (Style)FindResource("App.Swatch");
            foreach (var (name, hex) in AccentPresets)
            {
                AppearanceService.TryParseColor(hex, out var col);
                var rb = new RadioButton
                {
                    Style = style,
                    GroupName = "Accent",
                    Background = new SolidColorBrush(col),
                    Tag = AppearanceConfig.NormalizeHex(hex),
                    ToolTip = name,
                };
                rb.Checked += Swatch_Changed;
                SwatchPanel.Children.Add(rb);
            }
        }

        private void UpdateTilePreview()
        {
            var a = AppearanceService.Current;
            bool dark = AppearanceService.ResolvedTheme != AppTheme.Light;

            byte alpha = (byte)Math.Round(a.TileFillAlphaPercent / 100.0 * 255.0);
            Color tint = dark ? Color.FromRgb(0x13, 0x15, 0x19) : Color.FromRgb(0xEA, 0xEE, 0xF2);
            TilePreview.Background = new SolidColorBrush(Color.FromArgb(alpha, tint.R, tint.G, tint.B));
            TilePreview.CornerRadius = new CornerRadius(a.TileCornerRadius);
            TilePreview.BorderThickness = new Thickness(a.ShowTileBorder ? 1 : 0);
            TilePreview.BorderBrush = new SolidColorBrush(dark
                ? Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x40, 0x00, 0x00, 0x00));
        }

        // ---- navigation --------------------------------------------

        private void Nav_Changed(object sender, RoutedEventArgs e) => UpdatePageVisibility();

        private void UpdatePageVisibility()
        {
            if (GeneralPage is null) return;
            GeneralPage.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            AppearancePage.Visibility = NavAppearance.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            AboutPage.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- appearance handlers ----------------------------------

        private void Theme_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var t = ThemeLight.IsChecked == true ? AppTheme.Light
                  : ThemeDark.IsChecked == true ? AppTheme.Dark
                  : AppTheme.System;
            AppearanceService.Update(c => c.Theme = t);
        }

        private void Transparency_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int val = (int)Math.Round(e.NewValue);
            TransparencyValue.Text = val + "%";
            if (_loading) return;
            AppearanceService.Update(c => c.TileTransparencyPercent = val, persist: false);
            Debounce();
        }

        private void Corner_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int val = (int)Math.Round(e.NewValue);
            CornerValue.Text = val + " px";
            if (_loading) return;
            AppearanceService.Update(c => c.TileCornerRadius = val, persist: false);
            Debounce();
        }

        private void Blur_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppearanceService.Update(c => c.BlurBehindTiles = BlurCheck.IsChecked == true);
        }

        private void Border_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppearanceService.Update(c => c.ShowTileBorder = BorderCheck.IsChecked == true);
        }

        private void AccentSystem_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppearanceService.Update(c => c.UseSystemAccent = AccentSystemCheck.IsChecked == true);
        }

        private void Swatch_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (sender is not RadioButton rb || rb.Tag is not string hex) return;
            AppearanceService.Update(c => { c.UseSystemAccent = false; c.AccentColor = hex; });
        }

        private void Debounce()
        {
            _persistTimer.Stop();
            _persistTimer.Start();
        }

        // ---- updates (unchanged behaviour) -----------------------

        private void AutoUpdate_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            var c = _update.Config;
            c.Enabled = AutoUpdateCheck.IsChecked == true;
            c.CheckOnStartup = StartupCheck.IsChecked == true;
            c.Channel = TagOf(ChannelCombo) is "stable" ? "stable" : "nightly";
            if (double.TryParse(TagOf(IntervalCombo), out var h) && h > 0)
                c.CheckIntervalHours = h;

            StartupCheck.IsEnabled = c.Enabled;
            ChannelCombo.IsEnabled = c.Enabled;
            IntervalCombo.IsEnabled = c.Enabled;

            _update.ApplySettings();
        }

        private async void CheckNow_Click(object sender, RoutedEventArgs e)
        {
            CheckNowButton.IsEnabled = false;
            CheckStatusText.Text = "Checking…";
            try { await _update.CheckAsync(userInitiated: true); }
            finally { CheckNowButton.IsEnabled = true; }
        }

        private void OnUpdateStatus(string message) =>
            Dispatcher.BeginInvoke(new Action(() => CheckStatusText.Text = message));

        private void OnUpdateAvailable(Models.UpdateInfo info, bool userInitiated) =>
            Dispatcher.BeginInvoke(new Action(() => CheckStatusText.Text = "An update is available."));

        // ---- startup & desktop ----------------------------------

        private void RunAtSignIn_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            StartupRegistration.SetEnabled(RunAtSignInCheck.IsChecked == true);
            RunAtSignInCheck.IsChecked = StartupRegistration.IsEnabled;
        }

        private void ShellMenu_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _host.ToggleShellIntegration(ShellMenuCheck.IsChecked == true);

            _loading = true;
            ShellMenuCheck.IsChecked = _host.ShellIntegrationEnabled;
            _loading = false;
        }

        // ---- data / about links --------------------------------

        private void OpenDataDir_Click(object sender, RoutedEventArgs e) =>
            OpenPath(BucketStore.AppDataDir, isFolder: true);

        private void OpenLog_Click(object sender, RoutedEventArgs e)
        {
            var log = System.IO.Path.Combine(BucketStore.AppDataDir, "log.txt");
            if (System.IO.File.Exists(log)) OpenPath(log, isFolder: false);
            else OpenPath(BucketStore.AppDataDir, isFolder: true);
        }

        private void OpenRepo_Click(object sender, RoutedEventArgs e) =>
            OpenPath("https://github.com/" + Models.UpdateConfig.DefaultRepo, isFolder: false);

        private static void OpenPath(string target, bool isFolder)
        {
            try
            {
                if (isFolder) System.IO.Directory.CreateDirectory(target);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { Log.Error($"Open '{target}' failed", ex); }
        }

        // ---- helpers -------------------------------------------

        private static void SelectByTag(ComboBox combo, string tag)
        {
            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (string?)i.Tag == tag) ?? combo.Items[0];
        }

        private static string TagOf(ComboBox combo) =>
            (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}

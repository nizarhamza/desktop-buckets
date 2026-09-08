using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DesktopBuckets.Services;

namespace DesktopBuckets.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly UpdateService _update;
        private readonly IBucketHost _host;
        private bool _loading;

        public SettingsWindow(UpdateService update, IBucketHost host)
        {
            _loading = true; // suppress control events during construction + initial load
            _update = update;
            _host = host;
            InitializeComponent();

            // Both handlers are removed on Closed. The service outlives this window by
            // hours; a handler left attached would keep the closed window alive and
            // poke its torn-down visual tree on the next check.
            _update.UpToDateOrError += OnUpdateStatus;
            _update.UpdateAvailable += OnUpdateAvailable;

            Closed += (_, _) =>
            {
                _update.UpToDateOrError -= OnUpdateStatus;
                _update.UpdateAvailable -= OnUpdateAvailable;
            };

            LoadFromState();
        }

        private void LoadFromState()
        {
            _loading = true;

            VersionText.Text = "version " + UpdateService.FormatVersion(_update.CurrentVersion);

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

            _loading = false;
        }

        private void OpenDataDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.IO.Directory.CreateDirectory(BucketStore.AppDataDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(BucketStore.AppDataDir)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { Log.Error("Open data folder failed", ex); }
        }

        private static void SelectByTag(ComboBox combo, string tag)
        {
            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (string?)i.Tag == tag) ?? combo.Items[0];
        }

        private static string TagOf(ComboBox combo) =>
            (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        // ---- updates ---------------------------------------------------

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

        // ---- startup & desktop --------------------------------------

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

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}

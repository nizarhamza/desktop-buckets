using System;
using System.Threading;
using System.Windows;
using DesktopBuckets.Models;
using DesktopBuckets.Services;

namespace DesktopBuckets.Views
{
    public partial class UpdatePromptWindow : Window
    {
        private readonly UpdateService _service;
        private readonly UpdateInfo _info;
        private readonly CancellationTokenSource _cts = new();
        private bool _busy;

        public UpdatePromptWindow(UpdateService service, UpdateInfo info)
        {
            _service = service;
            _info = info;
            InitializeComponent();

            VersionText.Text = $"{UpdateService.FormatVersion(service.CurrentVersion)}  →  {info.DisplayVersion}";
            NotesText.Text = string.IsNullOrWhiteSpace(info.Notes)
                ? "No release notes were provided."
                : info.Notes!.Trim();

            if (!info.HasInstaller)
            {
                UpdateButton.IsEnabled = false;
                StatusText.Text = "This release has no installer attached.";
                StatusText.Visibility = Visibility.Visible;
            }

            Closing += (_, e) => { if (_busy) e.Cancel = true; };
        }

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            UpdateButton.IsEnabled = LaterButton.IsEnabled = SkipButton.IsEnabled = false;
            DownloadBar.Visibility = Visibility.Visible;
            StatusText.Visibility = Visibility.Visible;
            StatusText.Text = "Downloading…";

            var progress = new Progress<double>(p =>
            {
                DownloadBar.Value = p;
                StatusText.Text = $"Downloading… {p:P0}";
            });

            var ok = await _service.DownloadAndLaunchAsync(_info, progress, _cts.Token);

            if (ok)
            {
                StatusText.Text = "Starting installer…";
                // The service shuts the app down; nothing more to do here.
            }
            else
            {
                _busy = false;
                UpdateButton.IsEnabled = LaterButton.IsEnabled = SkipButton.IsEnabled = true;
                DownloadBar.Visibility = Visibility.Collapsed;
                StatusText.Text = "Update failed — see log.txt. You can retry or download it from GitHub.";
            }
        }

        private void Later_Click(object sender, RoutedEventArgs e)
        {
            _service.SnoozeFor(TimeSpan.FromHours(24));
            Close();
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            _service.Skip(_info);
            Close();
        }
    }
}

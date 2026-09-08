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
        private CancellationTokenSource? _cts;
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

            // Closing while a download is in flight cancels it rather than being vetoed —
            // a stalled download must never leave the user with a window they can't shut.
            Closing += (_, _) => _cts?.Cancel();
            Closed += (_, _) => { _cts?.Dispose(); _cts = null; };
        }

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            SetBusyUi(true);
            StatusText.Text = "Downloading…";

            var progress = new Progress<double>(p =>
            {
                DownloadBar.Value = p;
                StatusText.Text = $"Downloading… {p:P0}";
            });

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            bool ok;
            try { ok = await _service.DownloadAndLaunchAsync(_info, progress, token); }
            finally { _busy = false; }

            if (ok)
            {
                StatusText.Text = "Starting installer…";
                // The service shuts the app down; nothing more to do here.
                return;
            }

            if (!IsLoaded) return; // window was closed mid-download
            SetBusyUi(false);
            StatusText.Text = token.IsCancellationRequested
                ? "Download cancelled."
                : "Update failed — see log.txt. You can retry or download it from GitHub.";
        }

        private void SetBusyUi(bool busy)
        {
            UpdateButton.IsEnabled = LaterButton.IsEnabled = SkipButton.IsEnabled = !busy;
            CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            DownloadBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            DownloadBar.Value = 0;
            StatusText.Visibility = Visibility.Visible;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            CancelButton.IsEnabled = false;
            StatusText.Text = "Cancelling…";
            _cts?.Cancel();
            CancelButton.IsEnabled = true;
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

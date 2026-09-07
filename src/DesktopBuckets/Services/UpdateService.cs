using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using DesktopBuckets.Models;

namespace DesktopBuckets.Services
{
    /// <summary>
    /// Polls a GitHub release channel for a newer build and raises
    /// <see cref="UpdateAvailable"/>. On request it downloads the installer and hands
    /// off to it silently, then the app exits so files can be replaced.
    /// </summary>
    public sealed class UpdateService : IDisposable
    {
        private static string ConfigPath => Path.Combine(BucketStore.AppDataDir, "update.json");
        private static string StatePath => Path.Combine(BucketStore.AppDataDir, "update-state.json");

        private readonly Dispatcher _dispatcher;
        private readonly Version _current;
        private readonly UpdateConfig _config;
        private UpdateState _state;
        private DispatcherTimer? _timer;
        private bool _checking;
        private bool _promptedThisRun;

        /// <summary>Raised on the UI thread. <c>userInitiated</c> is true for a manual check.</summary>
        public event Action<UpdateInfo, bool>? UpdateAvailable;

        /// <summary>Raised on the UI thread after a manual check that found nothing / failed.</summary>
        public event Action<string>? UpToDateOrError;

        public Version CurrentVersion => _current;

        /// <summary>Live settings. Mutate, then call <see cref="ApplySettings"/> to persist and re-arm.</summary>
        public UpdateConfig Config => _config;

        public UpdateService(Dispatcher dispatcher, Version currentVersion)
        {
            _dispatcher = dispatcher;
            _current = currentVersion;
            _config = JsonUtil.Read<UpdateConfig>(ConfigPath) ?? new UpdateConfig();
            _state = JsonUtil.Read<UpdateState>(StatePath) ?? new UpdateState();
        }

        public void Start()
        {
            StartPolling();

            if (_config.Enabled && _config.CheckOnStartup)
            {
                _ = _dispatcher.BeginInvoke(new Action(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(12)).ConfigureAwait(true);
                    await CheckAsync(userInitiated: false).ConfigureAwait(true);
                }), DispatcherPriority.ApplicationIdle);
            }
        }

        private void StartPolling()
        {
            _timer?.Stop();
            _timer = null;

            if (!_config.Enabled)
            {
                Log.Info("Updater disabled by config.");
                return;
            }

            var interval = TimeSpan.FromHours(Math.Max(0.25, _config.CheckIntervalHours));
            _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = interval };
            _timer.Tick += (_, _) => _ = CheckAsync(userInitiated: false);
            _timer.Start();
        }

        /// <summary>Persist <see cref="Config"/> to <c>update.json</c> and re-arm polling.
        /// Clears any "skip"/"remind me later" so a channel change can prompt again.</summary>
        public void ApplySettings()
        {
            try { JsonUtil.Write(ConfigPath, _config); }
            catch (Exception ex) { Log.Error("Saving update settings failed", ex); }

            _promptedThisRun = false;
            _state.SkippedVersion = null;
            _state.SnoozeUntilUtc = DateTime.MinValue;
            SaveState();

            StartPolling();
        }

        public async Task CheckAsync(bool userInitiated)
        {
            if (_checking) return;
            _checking = true;
            try
            {
                UpdateInfo? info = await FetchLatestAsync().ConfigureAwait(true);

                _state.LastCheckUtc = DateTime.UtcNow;
                SaveState();

                if (info == null)
                {
                    if (userInitiated) Raise(UpToDateOrError, "Could not reach GitHub to check for updates.");
                    return;
                }

                if (info.Version <= _current)
                {
                    Log.Info($"Update check: current {_current} is up to date (channel latest {info.Version}).");
                    if (userInitiated)
                        Raise(UpToDateOrError, $"You're on the latest version ({FormatVersion(_current)}).");
                    return;
                }

                if (!userInitiated)
                {
                    if (string.Equals(_state.SkippedVersion, info.DisplayVersion, StringComparison.OrdinalIgnoreCase))
                        return;
                    if (DateTime.UtcNow < _state.SnoozeUntilUtc) return;
                    if (_promptedThisRun) return;
                }

                _promptedThisRun = true;
                Log.Info($"Update available: {_current} -> {info.Version} ({info.TagName}).");
                _ = _dispatcher.BeginInvoke(new Action(() => UpdateAvailable?.Invoke(info, userInitiated)));
            }
            catch (Exception ex)
            {
                Log.Error("Update check failed", ex);
                if (userInitiated) Raise(UpToDateOrError, "Update check failed. See log.txt for details.");
            }
            finally
            {
                _checking = false;
            }
        }

        // ---- "Later" / "Skip" ------------------------------------------

        public void SnoozeFor(TimeSpan span)
        {
            _state.SnoozeUntilUtc = DateTime.UtcNow + span;
            SaveState();
        }

        public void Skip(UpdateInfo info)
        {
            _state.SkippedVersion = info.DisplayVersion;
            SaveState();
        }

        // ---- fetch ---------------------------------------------------

        private HttpClient CreateClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopBuckets-Updater/1.0");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            if (!string.IsNullOrWhiteSpace(_config.Token))
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _config.Token!.Trim());
            return http;
        }

        private async Task<UpdateInfo?> FetchLatestAsync()
        {
            var channel = (_config.Channel ?? "nightly").Trim().ToLowerInvariant();
            var endpoint = channel == "stable"
                ? $"https://api.github.com/repos/{_config.Repo}/releases/latest"
                : $"https://api.github.com/repos/{_config.Repo}/releases/tags/nightly";

            using var http = CreateClient();
            using var resp = await http.GetAsync(endpoint).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Error($"GitHub releases request {endpoint} -> {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string? notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;

            var version = ParseVersion(name) ?? ParseVersion(tag);
            if (version == null)
            {
                Log.Error($"Could not parse a version from release name='{name}' tag='{tag}'.");
                return null;
            }

            string? assetName = null, assetApi = null, assetBrowser = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var an = a.TryGetProperty("name", out var ann) ? ann.GetString() : null;
                    if (an == null || !an.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    assetName = an;
                    assetApi = a.TryGetProperty("url", out var au) ? au.GetString() : null;
                    assetBrowser = a.TryGetProperty("browser_download_url", out var bu) ? bu.GetString() : null;
                    break;
                }
            }

            return new UpdateInfo
            {
                Version = version,
                DisplayVersion = FormatVersion(version),
                TagName = string.IsNullOrEmpty(tag) ? name : tag,
                Notes = notes,
                AssetName = assetName,
                AssetApiUrl = assetApi,
                AssetBrowserUrl = assetBrowser,
            };
        }

        // ---- download + hand off ------------------------------------

        public async Task<bool> DownloadAndLaunchAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
        {
            if (!info.HasInstaller)
            {
                Raise(UpToDateOrError, "This release has no installer attached.");
                return false;
            }

            var target = Path.Combine(Path.GetTempPath(),
                info.AssetName ?? $"DesktopBuckets-Setup-{info.DisplayVersion}.exe");

            try
            {
                using var http = CreateClient();

                // Prefer the API asset URL (needed for private repos); it also works public.
                var url = info.AssetApiUrl ?? info.AssetBrowserUrl!;
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (info.AssetApiUrl != null)
                {
                    req.Headers.Accept.Clear();
                    req.Headers.Accept.ParseAdd("application/octet-stream");
                }

                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                var totalBytes = resp.Content.Headers.ContentLength ?? -1L;
                await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    long read = 0;
                    int r;
                    while ((r = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, r), ct).ConfigureAwait(false);
                        read += r;
                        if (totalBytes > 0) progress?.Report((double)read / totalBytes);
                    }
                }

                if (new FileInfo(target).Length < 1_000_000)
                    throw new IOException($"Downloaded installer is implausibly small ({new FileInfo(target).Length} bytes).");

                Log.Info($"Downloaded {target}; launching silent installer and exiting.");
                Process.Start(new ProcessStartInfo(target)
                {
                    UseShellExecute = true,
                    Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART",
                });

                _ = _dispatcher.BeginInvoke(new Action(() =>
                    System.Windows.Application.Current?.Shutdown()));
                return true;
            }
            catch (OperationCanceledException)
            {
                TryDelete(target);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("Update download/launch failed", ex);
                Raise(UpToDateOrError, "Downloading the update failed. See log.txt.");
                TryDelete(target);
                return false;
            }
        }

        // ---- helpers ----------------------------------------------

        private static Version? ParseVersion(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
            int plus = s.IndexOf('+'); if (plus >= 0) s = s[..plus];
            int dash = s.IndexOf('-'); if (dash >= 0) s = s[..dash];
            return Version.TryParse(s, out var v) ? Normalize(v) : null;
        }

        private static Version Normalize(Version v) =>
            new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build, v.Revision < 0 ? 0 : v.Revision);

        public static string FormatVersion(Version v) =>
            v.Revision > 0 ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : $"{v.Major}.{v.Minor}.{v.Build}";

        private void SaveState()
        {
            try { JsonUtil.Write(StatePath, _state); } catch (Exception ex) { Log.Error("Save update-state failed", ex); }
        }

        private void Raise(Action<string>? handler, string message) =>
            _ = _dispatcher.BeginInvoke(new Action(() => handler?.Invoke(message)));

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
        }

        public void Dispose() => _timer?.Stop();
    }
}

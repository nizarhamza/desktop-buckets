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

        /// <summary>SHA-1 thumbprint of the certificate every published installer is
        /// signed with (packaging/DesktopBuckets.cer). A downloaded installer that is
        /// unsigned, tampered with, or signed by anyone else is never launched. Rotate
        /// this together with the certificate.</summary>
        internal const string InstallerSignerThumbprint = "9220A7BFAAA4CA0D714913791D27D31709280CE5";

        public UpdateService(Dispatcher dispatcher, Version currentVersion)
        {
            _dispatcher = dispatcher;
            _current = currentVersion;
            _config = JsonUtil.Read<UpdateConfig>(ConfigPath) ?? new UpdateConfig();
            _state = JsonUtil.Read<UpdateState>(StatePath) ?? new UpdateState();

            if (!_config.IsDefaultRepo)
                Log.Error($"update.json points the updater at '{_config.Repo}' instead of '{UpdateConfig.DefaultRepo}'. " +
                          "Installers from there must still carry the pinned signature, but check that this is intended.");

            // A token pasted in plaintext is re-saved DPAPI-protected right away.
            try
            {
                if (_config.ProtectToken())
                {
                    JsonUtil.Write(ConfigPath, _config);
                    Log.Info("update.json: GitHub token is now stored encrypted (DPAPI, current user).");
                }
            }
            catch (Exception ex) { Log.Error("Protecting the GitHub token failed", ex); }

            var channel = NormalizeChannel(_config.Channel);
            if (_state.LastChannel == null)
            {
                _state.LastChannel = channel;
            }
            else if (!string.Equals(_state.LastChannel, channel, StringComparison.Ordinal))
            {
                // update.json was edited by hand between runs.
                _state.LastChannel = channel;
                _state.ChannelSwitchPending = true;
            }
        }

        internal static string NormalizeChannel(string? channel) =>
            string.Equals(channel?.Trim(), "stable", StringComparison.OrdinalIgnoreCase) ? "stable" : "nightly";

        public enum Decision { UpToDate, Offer, OfferDowngrade }

        /// <summary>What to do with the channel's latest build. Ordinarily only a newer
        /// version is offered; after a channel switch the target channel's build is
        /// offered whatever its number, because nightly build numbers (<c>0.1.&lt;run&gt;</c>)
        /// climb past stable tags and would otherwise pin the user to nightly forever.</summary>
        internal static Decision Decide(Version current, Version candidate, bool channelSwitchPending)
        {
            if (candidate == current) return Decision.UpToDate;
            if (candidate > current) return Decision.Offer;
            return channelSwitchPending ? Decision.OfferDowngrade : Decision.UpToDate;
        }

        public void Start()
        {
            // A successful update can't delete its own installer (it's running when we
            // exit), so each one leaves ~50 MB in %TEMP%. Sweep old ones now.
            try
            {
                int n = CleanStaleDownloads(Path.GetTempPath(), TimeSpan.FromHours(1));
                if (n > 0) Log.Info($"Removed {n} stale update download folder(s) from %TEMP%.");
            }
            catch (Exception ex) { Log.Error("Stale download cleanup failed", ex); }

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
            JsonUtil.Write(ConfigPath, _config);

            var channel = NormalizeChannel(_config.Channel);
            if (!string.Equals(_state.LastChannel, channel, StringComparison.Ordinal))
            {
                _state.LastChannel = channel;
                _state.ChannelSwitchPending = true;
                Log.Info($"Update channel switched to '{channel}'; its latest build will be offered regardless of version.");
            }

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

                var decision = Decide(_current, info.Version, _state.ChannelSwitchPending);
                if (decision == Decision.UpToDate)
                {
                    if (_state.ChannelSwitchPending && info.Version == _current)
                    {
                        _state.ChannelSwitchPending = false; // already on the target channel's build
                        SaveState();
                    }
                    Log.Info($"Update check: current {_current} is up to date (channel latest {info.Version}).");
                    if (userInitiated)
                        Raise(UpToDateOrError, $"You're on the latest version ({FormatVersion(_current)}).");
                    return;
                }
                info.IsDowngrade = decision == Decision.OfferDowngrade;

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
            var token = _config.ResolveToken();
            if (!string.IsNullOrWhiteSpace(token))
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
            return http;
        }

        private async Task<UpdateInfo?> FetchLatestAsync()
        {
            var channel = NormalizeChannel(_config.Channel);
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
                Repo = _config.Repo,
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

            // AssetName comes from the GitHub API response: keep only the file name so a
            // server-supplied value can't steer the path anywhere else.
            var assetName = Path.GetFileName(info.AssetName ?? "");
            if (string.IsNullOrWhiteSpace(assetName) || !assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                assetName = $"DesktopBuckets-Setup-{info.DisplayVersion}.exe";

            // A fresh, unpredictable directory: nothing else can pre-create or swap the
            // file between the checks below and Process.Start.
            var dir = Path.Combine(Path.GetTempPath(), DownloadDirPrefix + Guid.NewGuid().ToString("N"));
            var target = Path.Combine(dir, assetName);

            try
            {
                Directory.CreateDirectory(dir);
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

                var problem = VerifyInstaller(target);
                if (problem != null)
                {
                    Log.Error($"Refusing to run downloaded installer {target}: {problem}");
                    Raise(UpToDateOrError, "The downloaded update failed its signature check and was not installed.");
                    TryDeleteDir(dir);
                    return false;
                }

                Log.Info($"Downloaded {target} (signature OK); launching silent installer and exiting.");
                Process.Start(new ProcessStartInfo(target)
                {
                    UseShellExecute = true,
                    Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART",
                });

                _state.ChannelSwitchPending = false;
                SaveState();

                _ = _dispatcher.BeginInvoke(new Action(() =>
                    System.Windows.Application.Current?.Shutdown()));
                return true;
            }
            catch (OperationCanceledException)
            {
                TryDeleteDir(dir);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("Update download/launch failed", ex);
                Raise(UpToDateOrError, "Downloading the update failed. See log.txt.");
                TryDeleteDir(dir);
                return false;
            }
        }

        /// <summary>Null when the file carries an intact Authenticode signature from the
        /// pinned certificate; otherwise a reason it must not be run.</summary>
        internal static string? VerifyInstaller(string path)
        {
            var status = Interop.Authenticode.Verify(path, out int hr);
            switch (status)
            {
                case Interop.Authenticode.Status.Valid:
                case Interop.Authenticode.Status.IntactUntrustedChain:
                    break;
                case Interop.Authenticode.Status.NoSignature:
                    return "the file is not signed";
                case Interop.Authenticode.Status.Tampered:
                    return "the file does not match its signature";
                default:
                    return $"WinVerifyTrust returned 0x{hr:X8}";
            }

            var thumb = Interop.Authenticode.SignerThumbprint(path);
            if (!string.Equals(thumb, InstallerSignerThumbprint, StringComparison.OrdinalIgnoreCase))
                return $"signed by an unexpected certificate (thumbprint {thumb ?? "none"})";
            return null;
        }

        private static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }

        internal const string DownloadDirPrefix = "DesktopBuckets-update-";

        /// <summary>Deletes download folders under <paramref name="tempRoot"/> last
        /// touched more than <paramref name="olderThan"/> ago. Returns how many went.</summary>
        internal static int CleanStaleDownloads(string tempRoot, TimeSpan olderThan)
        {
            if (!Directory.Exists(tempRoot)) return 0;
            int removed = 0;
            var cutoff = DateTime.UtcNow - olderThan;
            foreach (var dir in Directory.EnumerateDirectories(tempRoot, DownloadDirPrefix + "*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) > cutoff) continue;
                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // still in use (installer running) or locked — next time
                }
            }
            return removed;
        }

        // ---- helpers ----------------------------------------------

        internal static Version? ParseVersion(string? s)
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

        public void Dispose() => _timer?.Stop();
    }
}

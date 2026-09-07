using System;
using System.Text.Json.Serialization;

namespace DesktopBuckets.Models
{
    /// <summary>
    /// Auto-update settings, read from <c>%APPDATA%\DesktopBuckets\update.json</c>.
    /// The file is optional — a sensible default targets this project's public releases.
    /// A <see cref="Token"/> is only needed while the GitHub repo is private; put a
    /// fine-grained PAT with <c>Contents: Read-only</c> on just this repo in the file,
    /// never in the binary.
    /// </summary>
    public sealed class UpdateConfig
    {
        public bool Enabled { get; set; } = true;

        /// <summary><c>owner/repo</c> on github.com.</summary>
        public string Repo { get; set; } = "nizarhamza/desktop-buckets";

        /// <summary><c>nightly</c> = the rolling build published on every push to main;
        /// <c>stable</c> = the latest tagged release.</summary>
        public string Channel { get; set; } = "nightly";

        /// <summary>Optional GitHub token (needed only for a private repo).</summary>
        public string? Token { get; set; }

        public double CheckIntervalHours { get; set; } = 6;

        /// <summary>Check shortly after launch as well as on the interval.</summary>
        public bool CheckOnStartup { get; set; } = true;
    }

    /// <summary>Mutable state the updater keeps to itself, in
    /// <c>%APPDATA%\DesktopBuckets\update-state.json</c>.</summary>
    public sealed class UpdateState
    {
        public DateTime LastCheckUtc { get; set; }

        /// <summary>Version the user chose to skip; never re-prompted for it.</summary>
        public string? SkippedVersion { get; set; }

        /// <summary>"Remind me later" — suppress prompts until this time.</summary>
        public DateTime SnoozeUntilUtc { get; set; }
    }

    /// <summary>A newer build found on the configured channel.</summary>
    public sealed class UpdateInfo
    {
        public required Version Version { get; init; }
        public required string DisplayVersion { get; init; }
        public required string TagName { get; init; }
        public string? Notes { get; init; }
        public string? AssetName { get; init; }

        /// <summary>GitHub API asset URL (works for private repos with a token +
        /// <c>Accept: application/octet-stream</c>).</summary>
        public string? AssetApiUrl { get; init; }

        /// <summary>Public direct download URL (used when no token is configured).</summary>
        public string? AssetBrowserUrl { get; init; }

        [JsonIgnore]
        public bool HasInstaller =>
            !string.IsNullOrEmpty(AssetApiUrl) || !string.IsNullOrEmpty(AssetBrowserUrl);
    }
}

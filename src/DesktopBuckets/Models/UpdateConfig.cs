using System;
using System.Text.Json.Serialization;

namespace DesktopBuckets.Models
{
    /// <summary>
    /// Auto-update settings, read from <c>%USERPROFILE%\Desktop Buckets\.app\update.json</c>.
    /// The file is optional — a sensible default targets this project's public releases.
    /// A <see cref="Token"/> is only needed while the GitHub repo is private; put a
    /// fine-grained PAT with <c>Contents: Read-only</c> on just this repo in the file,
    /// never in the binary.
    /// </summary>
    public sealed class UpdateConfig
    {
        public const string DefaultRepo = "nizarhamza/desktop-buckets";

        public bool Enabled { get; set; } = true;

        /// <summary><c>owner/repo</c> on github.com. Anything other than
        /// <see cref="DefaultRepo"/> is logged loudly at startup and shown in the update
        /// prompt: this one line in a user-writable file decides where installers come from.</summary>
        public string Repo { get; set; } = DefaultRepo;

        [JsonIgnore]
        public bool IsDefaultRepo =>
            string.Equals(Repo?.Trim(), DefaultRepo, StringComparison.OrdinalIgnoreCase);

        /// <summary><c>nightly</c> = the rolling build published on every push to main;
        /// <c>stable</c> = the latest tagged release.</summary>
        public string Channel { get; set; } = "nightly";

        /// <summary>Optional GitHub token (needed only for a private repo). On disk it is
        /// kept DPAPI-encrypted for the current user (<c>dpapi:&lt;base64&gt;</c>); a
        /// plaintext value pasted into update.json is encrypted on the next start, so a
        /// stray profile backup doesn't carry a live credential. Use
        /// <see cref="ResolveToken"/> to get the usable value.</summary>
        public string? Token { get; set; }

        private const string DpapiPrefix = "dpapi:";

        [JsonIgnore]
        public bool TokenIsProtected => Token?.StartsWith(DpapiPrefix, StringComparison.Ordinal) == true;

        /// <summary>The plaintext token, or null when unset / undecryptable (a token
        /// protected on another machine or account can't be recovered here).</summary>
        public string? ResolveToken()
        {
            if (string.IsNullOrWhiteSpace(Token)) return null;
            if (!TokenIsProtected) return Token.Trim();
            try
            {
                var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(Token.Substring(DpapiPrefix.Length)),
                    null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Replace a plaintext <see cref="Token"/> with its protected form.
        /// Returns true when the value changed (caller should persist).</summary>
        public bool ProtectToken()
        {
            if (string.IsNullOrWhiteSpace(Token) || TokenIsProtected) return false;
            var bytes = System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(Token.Trim()),
                null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            Token = DpapiPrefix + Convert.ToBase64String(bytes);
            return true;
        }

        public double CheckIntervalHours { get; set; } = 6;

        /// <summary>Check shortly after launch as well as on the interval.</summary>
        public bool CheckOnStartup { get; set; } = true;
    }

    /// <summary>Mutable state the updater keeps to itself, in
    /// <c>%USERPROFILE%\Desktop Buckets\.app\update-state.json</c>.</summary>
    public sealed class UpdateState
    {
        public DateTime LastCheckUtc { get; set; }

        /// <summary>Version the user chose to skip; never re-prompted for it.</summary>
        public string? SkippedVersion { get; set; }

        /// <summary>"Remind me later" — suppress prompts until this time.</summary>
        public DateTime SnoozeUntilUtc { get; set; }

        /// <summary>Channel the last check ran against; a change flips
        /// <see cref="ChannelSwitchPending"/>.</summary>
        public string? LastChannel { get; set; }

        /// <summary>Set when the user switches channel. While set, the target channel's
        /// latest build is offered even if its version number is <i>lower</i> than the
        /// running one (nightly <c>0.1.&lt;run&gt;</c> climbs past stable tags), so
        /// nightly → stable is a reinstall, not a dead end. Cleared once an install
        /// starts or the versions match.</summary>
        public bool ChannelSwitchPending { get; set; }
    }

    /// <summary>A newer build found on the configured channel.</summary>
    public sealed class UpdateInfo
    {
        public required Version Version { get; init; }
        public required string DisplayVersion { get; init; }
        public required string TagName { get; init; }
        public string? Notes { get; init; }
        public string? AssetName { get; init; }

        /// <summary><c>owner/repo</c> the release came from (for the prompt).</summary>
        public string? Repo { get; init; }

        /// <summary>True when this build is older than the running one and is being
        /// offered anyway because of a channel switch.</summary>
        public bool IsDowngrade { get; set; }

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

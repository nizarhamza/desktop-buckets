using DesktopBuckets.Models;

namespace DesktopBuckets.Services
{
    /// <summary>Bucket lifecycle operations a tile window can ask its host to perform.</summary>
    public interface IBucketHost
    {
        void PromptCreateBucket(string? parentFolder = null);
        void RenameBucket(Bucket bucket, string newName);
        void DeleteBucket(Bucket bucket);
        void ToggleShellIntegration(bool enabled);
        bool ShellIntegrationEnabled { get; }

        /// <summary>Tell the user something they asked for visibly didn't happen (a
        /// tray balloon). The app is silent on success by design, not on failure.</summary>
        void Notify(string message);
    }
}

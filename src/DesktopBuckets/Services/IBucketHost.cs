using System.Collections.Generic;
using System.Windows;
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

        /// <summary>Opens (or focuses, if already open) the Settings window. Previously
        /// reachable only via the tray icon; also offered on a tile's own right-click
        /// menu so it isn't hidden behind a separate icon the user has to go find.</summary>
        void OpenSettings();

        /// <summary>Tell the user something they asked for visibly didn't happen (a
        /// tray balloon). The app is silent on success by design, not on failure.</summary>
        void Notify(string message);

        /// <summary>Screen-pixel rects (Left, Top, Width, Height) of every other live
        /// tile window besides <paramref name="exclude"/>. Used at placement time so a
        /// freshly created/dragged-in tile can avoid landing on top of one that's
        /// already there instead of just stacking blindly.</summary>
        IEnumerable<Rect> OtherTileRects(object exclude);
    }
}

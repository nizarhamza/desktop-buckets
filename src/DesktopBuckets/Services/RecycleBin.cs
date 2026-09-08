using DesktopBuckets.Interop;

namespace DesktopBuckets.Services
{
    internal static class RecycleBin
    {
        /// <summary>Sends a file or directory to the Recycle Bin (undoable). Returns true
        /// when the item is gone; false if the shell failed or the user cancelled.</summary>
        public static bool Send(string path)
        {
            var r = ShellFileOperations.Recycle(path);
            if (!r.Succeeded)
                Log.Error($"Recycle failed for '{path}': {(r.Aborted ? "cancelled by user" : r.Error)}");
            return r.Succeeded;
        }
    }
}

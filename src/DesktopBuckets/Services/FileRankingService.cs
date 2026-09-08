using System.Collections.Generic;
using System.Linq;
using DesktopBuckets.Models;

namespace DesktopBuckets.Services
{
    /// <summary>
    /// Decides which files a closed tile shows, and in what order.
    ///
    /// Priority:
    ///   1. Pinned files, in the bucket's pin order (oldest pin first).
    ///   2. Remaining slots filled by most-recent activity
    ///      (max of last-modified and last-opened-via-tile), newest first.
    ///   3. Fewer files than slots -> just return what exists.
    /// </summary>
    public static class FileRankingService
    {
        public static IReadOnlyList<BucketFile> SelectVisible(
            Bucket bucket, IReadOnlyList<BucketFile> files)
        {
            int slots = bucket.Config.SlotCount;
            if (slots <= 0 || files.Count == 0)
                return System.Array.Empty<BucketFile>();

            // TryAdd, not ToDictionary: a case-sensitive directory (Windows 10+ supports
            // per-directory case sensitivity) can hold Notes.txt and notes.txt at once,
            // and ToDictionary would throw on the duplicate key every watcher tick.
            var byRel = new Dictionary<string, BucketFile>(files.Count, System.StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) byRel.TryAdd(f.RelativePath, f);

            var ordered = new List<BucketFile>(slots);
            var used = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            // 1. pinned, in stored order
            foreach (var rel in bucket.Config.Pinned)
            {
                if (ordered.Count >= slots) break;
                if (byRel.TryGetValue(rel, out var f) && used.Add(f.RelativePath))
                    ordered.Add(f);
            }

            // 2. fill with most-recently-active non-pinned files
            if (ordered.Count < slots)
            {
                var rest = files
                    .Where(f => !used.Contains(f.RelativePath))
                    .OrderByDescending(f => f.EffectiveActivityUtc)
                    .ThenBy(f => f.Name, System.StringComparer.OrdinalIgnoreCase);

                foreach (var f in rest)
                {
                    if (ordered.Count >= slots) break;
                    ordered.Add(f);
                }
            }

            return ordered;
        }
    }
}

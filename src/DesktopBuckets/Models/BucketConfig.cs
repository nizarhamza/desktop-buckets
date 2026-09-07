using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DesktopBuckets.Models
{
    /// <summary>
    /// Per-bucket state, persisted as a hidden <c>.bucket.json</c> file inside the
    /// bucket's backing folder. Everything here survives the folder being moved.
    /// File references are stored relative to the bucket folder.
    /// </summary>
    public sealed class BucketConfig
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Display name. Normally equals the folder name; kept separately
        /// so a rename that fails to move the folder still shows the intended label.</summary>
        public string Name { get; set; } = "Bucket";

        /// <summary>Bucket-relative paths, in pin order (oldest pin first).
        /// Unlimited entries; only the first <see cref="SlotCount"/> are ever shown.</summary>
        public List<string> Pinned { get; set; } = new();

        /// <summary>Bucket-relative path -&gt; last time the tile launched that file.
        /// Combined with LastWriteTime to rank "recently worked" files.</summary>
        public Dictionary<string, DateTime> LastOpenedUtc { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public int SlotCount { get; set; } = 4;

        public double? X { get; set; }
        public double? Y { get; set; }

        /// <summary>When true the tile cannot be dragged and the folder is not renamed on rename.</summary>
        public bool Locked { get; set; }

        [JsonIgnore]
        public bool HasStoredPosition => X.HasValue && Y.HasValue;
    }
}

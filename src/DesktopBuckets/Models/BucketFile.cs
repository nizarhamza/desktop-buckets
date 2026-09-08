using System;

namespace DesktopBuckets.Models
{
    /// <summary>An immutable snapshot of one entry — a file or a sub-folder — that lives
    /// directly inside a bucket folder.</summary>
    public sealed class BucketFile
    {
        public required string FullPath { get; init; }
        public required string RelativePath { get; init; }
        public required string Name { get; init; }

        /// <summary>True when this entry is a sub-folder rather than a file. The tile
        /// renders it with the shell's folder icon and opens it in Explorer.</summary>
        public bool IsDirectory { get; init; }

        public DateTime LastWriteUtc { get; init; }
        public DateTime LastOpenedViaTileUtc { get; init; }
        public bool IsPinned { get; init; }

        /// <summary>Ranking key for the "recently worked" fill: the later of the file's
        /// own modification time and the last time the tile opened it.</summary>
        public DateTime EffectiveActivityUtc =>
            LastOpenedViaTileUtc > LastWriteUtc ? LastOpenedViaTileUtc : LastWriteUtc;
    }
}

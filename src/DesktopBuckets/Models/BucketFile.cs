using System;

namespace DesktopBuckets.Models
{
    /// <summary>An immutable snapshot of one file that lives directly inside a bucket folder.</summary>
    public sealed class BucketFile
    {
        public required string FullPath { get; init; }
        public required string RelativePath { get; init; }
        public required string Name { get; init; }
        public DateTime LastWriteUtc { get; init; }
        public DateTime LastOpenedViaTileUtc { get; init; }
        public bool IsPinned { get; init; }

        /// <summary>Ranking key for the "recently worked" fill: the later of the file's
        /// own modification time and the last time the tile opened it.</summary>
        public DateTime EffectiveActivityUtc =>
            LastOpenedViaTileUtc > LastWriteUtc ? LastOpenedViaTileUtc : LastWriteUtc;
    }
}

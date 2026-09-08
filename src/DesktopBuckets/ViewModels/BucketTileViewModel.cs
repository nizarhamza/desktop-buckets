using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DesktopBuckets.Models;
using DesktopBuckets.Services;

namespace DesktopBuckets.ViewModels
{
    public sealed class BucketTileViewModel : ObservableObject
    {
        public Bucket Bucket { get; }

        public ObservableCollection<BucketFileViewModel> Slots { get; } = new();

        /// <summary>Raised after <see cref="Refresh"/> — the tile window relayouts / re-fits to it.</summary>
        public event Action? Refreshed;

        public BucketTileViewModel(Bucket bucket)
        {
            Bucket = bucket;
            Refresh();
        }

        public string Name => Bucket.Name;

        private int _columns = 2;
        public int Columns { get => _columns; private set => Set(ref _columns, value); }

        private int _rows = 2;
        public int Rows { get => _rows; private set => Set(ref _rows, value); }

        private bool _isEmpty = true;
        public bool IsEmpty { get => _isEmpty; private set => Set(ref _isEmpty, value); }

        private int _totalCount;
        public int TotalCount { get => _totalCount; private set => Set(ref _totalCount, value); }

        /// <summary>"+3 more" indicator when the bucket holds more files than it can show.</summary>
        public int OverflowCount => Math.Max(0, TotalCount - Slots.Count);
        public bool HasOverflow => OverflowCount > 0;

        /// <summary>Where the "+N" overflow count used to sit inline next to the bucket
        /// name, crowding it (worst on a narrow 1-row tile). Surfaced as a tooltip on the
        /// name instead — null (no tooltip at all, not an empty bubble) when nothing is
        /// hidden.</summary>
        public string? OverflowTooltip => HasOverflow
            ? (OverflowCount == 1 ? "1 more file not shown" : $"{OverflowCount} more files not shown")
            : null;

        public void Refresh()
        {
            var all = Bucket.EnumerateFiles();
            var visible = FileRankingService.SelectVisible(Bucket, all);

            ReconcileSlots(visible);

            TotalCount = all.Count;
            IsEmpty = Slots.Count == 0;
            RecomputeGrid();
            Raise(nameof(Name));
            Raise(nameof(OverflowCount));
            Raise(nameof(HasOverflow));
            Raise(nameof(OverflowTooltip));

            Refreshed?.Invoke();
        }

        /// <summary>Bring <see cref="Slots"/> in line with <paramref name="visible"/> by
        /// position, replacing only entries that actually changed. Clear-and-re-add fired
        /// a collection Reset on every watcher tick, which tears down and rebuilds every
        /// item container (and flickers); a keyed diff keeps untouched slots' visuals.</summary>
        private void ReconcileSlots(System.Collections.Generic.IReadOnlyList<BucketFile> visible)
        {
            for (int i = 0; i < visible.Count; i++)
            {
                var f = visible[i];
                if (i < Slots.Count)
                {
                    if (Slots[i].Represents(f)) continue;
                    Slots[i] = new BucketFileViewModel(f);   // Replace, not Reset
                }
                else
                {
                    Slots.Add(new BucketFileViewModel(f));
                }
            }
            while (Slots.Count > visible.Count)
                Slots.RemoveAt(Slots.Count - 1);
        }

        private void RecomputeGrid()
        {
            // Shape comes from a fixed set of 5 (see TileShape), not a formula on raw
            // item count — a stale/non-canonical stored SlotCount (from before this
            // shape set existed, or a future direct edit) still resolves to a sane
            // shape here rather than needing a migration step.
            var shape = TileShape.For(Bucket.Config.SlotCount);
            Columns = shape.Cols;
            Rows = shape.Rows;
        }

        // ---- file actions ------------------------------------------------

        /// <summary>Launches the file with its default handler. Returns an error message
        /// when nothing happened (file gone, no handler, locked), or null on success.</summary>
        public string? OpenFile(BucketFileViewModel vm)
        {
            if (!File.Exists(vm.FullPath))
            {
                Refresh();
                return $"{vm.Name} is no longer in the bucket.";
            }
            try
            {
                Process.Start(new ProcessStartInfo(vm.FullPath) { UseShellExecute = true });
                Bucket.RecordOpened(vm.RelativePath);
                Refresh();
                return null;
            }
            catch (Exception ex)
            {
                Log.Error($"OpenFile failed: {vm.FullPath}", ex);
                return $"Couldn't open {vm.Name}: {ex.Message}";
            }
        }

        public void TogglePin(BucketFileViewModel vm)
        {
            if (Bucket.IsPinned(vm.RelativePath)) Bucket.Unpin(vm.RelativePath);
            else Bucket.Pin(vm.RelativePath);
            Refresh();
        }

        public string? OpenContainingFolder()
        {
            try
            {
                Directory.CreateDirectory(Bucket.FolderPath);
                Process.Start(new ProcessStartInfo(Bucket.FolderPath) { UseShellExecute = true });
                return null;
            }
            catch (Exception ex)
            {
                Log.Error($"Open bucket folder failed: {Bucket.FolderPath}", ex);
                return $"Couldn't open the bucket folder: {ex.Message}";
            }
        }

        public string? RevealInExplorer(BucketFileViewModel vm)
        {
            try
            {
                if (File.Exists(vm.FullPath))
                {
                    Process.Start("explorer.exe", $"/select,\"{vm.FullPath}\"");
                    return null;
                }
                return OpenContainingFolder();
            }
            catch (Exception ex)
            {
                Log.Error($"Reveal in Explorer failed: {vm.FullPath}", ex);
                return $"Couldn't show {vm.Name} in Explorer: {ex.Message}";
            }
        }

        public void SetSlotCount(int n)
        {
            Bucket.SetSlotCount(n);
            Refresh();
        }
    }
}

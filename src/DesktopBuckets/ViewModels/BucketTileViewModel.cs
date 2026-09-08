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

        public void Refresh()
        {
            var all = Bucket.EnumerateFiles();
            var visible = FileRankingService.SelectVisible(Bucket, all);

            Slots.Clear();
            foreach (var f in visible)
                Slots.Add(new BucketFileViewModel(f));

            TotalCount = all.Count;
            IsEmpty = Slots.Count == 0;
            RecomputeGrid();
            Raise(nameof(Name));
            Raise(nameof(OverflowCount));
            Raise(nameof(HasOverflow));

            Refreshed?.Invoke();
        }

        private void RecomputeGrid()
        {
            int n = Math.Clamp(Bucket.Config.SlotCount, 1, 9);
            int cols = (int)Math.Ceiling(Math.Sqrt(n));
            int rows = (int)Math.Ceiling(n / (double)cols);
            Columns = cols;
            Rows = rows;
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

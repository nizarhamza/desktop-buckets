using System.Windows.Media;
using DesktopBuckets.Models;
using DesktopBuckets.Services;

namespace DesktopBuckets.ViewModels
{
    /// <summary>One icon slot in a tile.</summary>
    public sealed class BucketFileViewModel : ObservableObject
    {
        public BucketFile File { get; }

        public BucketFileViewModel(BucketFile file)
        {
            File = file;
            Icon = IconService.Instance.GetLargeIcon(file.FullPath, file.IsDirectory);
        }

        public string Name => File.Name;
        public string FullPath => File.FullPath;
        public string RelativePath => File.RelativePath;
        public bool IsPinned => File.IsPinned;
        public bool IsDirectory => File.IsDirectory;

        /// <summary>True when this slot already shows <paramref name="other"/> in every
        /// way the tile renders (path, name, kind, pin state, and the icon's source file).</summary>
        public bool Represents(BucketFile other) =>
            string.Equals(File.FullPath, other.FullPath, System.StringComparison.OrdinalIgnoreCase)
            && File.Name == other.Name
            && File.IsDirectory == other.IsDirectory
            && File.IsPinned == other.IsPinned
            && File.LastWriteUtc == other.LastWriteUtc; // a rewritten file may have a new icon (e.g. .exe/.ico)

        private ImageSource? _icon;
        public ImageSource? Icon
        {
            get => _icon;
            private set => Set(ref _icon, value);
        }
    }
}

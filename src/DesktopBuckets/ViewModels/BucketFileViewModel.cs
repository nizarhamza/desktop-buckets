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
            Icon = IconService.Instance.GetLargeIcon(file.FullPath);
        }

        public string Name => File.Name;
        public string FullPath => File.FullPath;
        public string RelativePath => File.RelativePath;
        public bool IsPinned => File.IsPinned;

        private ImageSource? _icon;
        public ImageSource? Icon
        {
            get => _icon;
            private set => Set(ref _icon, value);
        }
    }
}

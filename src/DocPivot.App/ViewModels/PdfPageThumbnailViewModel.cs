using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DocPivot.App.ViewModels;

public sealed partial class PdfPageThumbnailViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isCutBefore;

    public PdfPageThumbnailViewModel(int pageNumber, string thumbnailPath)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbnailPath);
        PageNumber = pageNumber;
        Thumbnail = LoadThumbnail(thumbnailPath);
    }

    public int PageNumber { get; }

    public bool CanCutBefore => PageNumber > 1;

    public string PageLabel => $"第 {PageNumber} 页";

    public ImageSource Thumbnail { get; }

    private static BitmapImage LoadThumbnail(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 136;
        image.UriSource = new Uri(System.IO.Path.GetFullPath(path), UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }
}

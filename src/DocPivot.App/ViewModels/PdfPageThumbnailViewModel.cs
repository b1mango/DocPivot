using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DocPivot.App.ViewModels;

public sealed partial class PdfPageThumbnailViewModel : ObservableObject
{
    private readonly BitmapSource _baseThumbnail;
    private ImageSource? _rotatedThumbnail;

    [ObservableProperty]
    private bool _isCutBefore;

    [ObservableProperty]
    private int _rotation;

    public PdfPageThumbnailViewModel(
        int pageNumber,
        string thumbnailPath,
        int sourceFileIndex = 0,
        int rotation = 0,
        string? sourceFilePath = null,
        string? sourceFileName = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbnailPath);
        PageNumber = pageNumber;
        SourceFileIndex = sourceFileIndex;
        SourceFilePath = sourceFilePath;
        SourceFileName = sourceFileName;
        _baseThumbnail = LoadThumbnail(thumbnailPath);
        Rotation = rotation;
    }

    public int PageNumber { get; }

    public int SourceFileIndex { get; private set; }

    public string? SourceFilePath { get; }

    public string? SourceFileName { get; }

    public bool CanCutBefore => PageNumber > 1;

    public string PageLabel => $"第 {PageNumber} 页";

    /// <summary>
    /// The thumbnail with the user-requested rotation applied at the bitmap level.
    /// Rotating the pixels (instead of a layout transform) keeps the page fully
    /// visible inside the preview card at every angle; layout transforms get
    /// clipped by the card's bounds and hide part of the page.
    /// </summary>
    public ImageSource Thumbnail => Rotation == 0
        ? _baseThumbnail
        : _rotatedThumbnail ??= new TransformedBitmap(_baseThumbnail, new RotateTransform(Rotation));

    public string DisplayLabel => string.IsNullOrWhiteSpace(SourceFileName)
        ? PageLabel
        : SourceFileName;

    public void UpdateSourceFileIndex(int index) => SourceFileIndex = index;

    public string RotationText => Rotation == 0 ? "0°" : $"{Rotation}°";

    public void RotateClockwise()
    {
        Rotation = (Rotation + 90) % 360;
    }

    partial void OnRotationChanged(int value)
    {
        _rotatedThumbnail = null;
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(RotationText));
    }

    private static BitmapImage LoadThumbnail(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!System.IO.File.Exists(fullPath))
        {
            throw new System.IO.FileNotFoundException("The rendered PDF thumbnail does not exist.", fullPath);
        }

        var image = new BitmapImage();
        using (var stream = System.IO.File.OpenRead(fullPath))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 256;
            image.StreamSource = stream;
            image.EndInit();
        }

        image.Freeze();
        return image;
    }
}

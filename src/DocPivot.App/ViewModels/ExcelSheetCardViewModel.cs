using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DocPivot.App.ViewModels;

/// <summary>One worksheet on the Excel split board: a first-page preview the user ticks to export.</summary>
public sealed partial class ExcelSheetCardViewModel : ObservableObject
{
    private const int PreviewDecodeWidth = 320;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private ImageSource? _thumbnail;

    [ObservableProperty]
    private bool _isPreviewPending = true;

    [ObservableProperty]
    private string _detailText = string.Empty;

    public ExcelSheetCardViewModel(string name, int position, bool isHidden)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Position = position;
        IsHidden = isHidden;
        // Hidden sheets are usually helpers (lookups, raw data); they start unticked.
        _isSelected = !isHidden;
    }

    public string Name { get; }

    public int Position { get; }

    public bool IsHidden { get; }

    public bool HasThumbnail => Thumbnail is not null;

    public void ShowPreview(string? imagePath, int? pageCount)
    {
        Thumbnail = imagePath is not null && File.Exists(imagePath) ? Load(imagePath) : null;
        DetailText = pageCount switch
        {
            // Excel only prints visible sheets, so a hidden one never gets a picture.
            null or 0 when Thumbnail is null => IsHidden ? "隐藏工作表不生成预览" : "空白或无法预览",
            null or 0 => string.Empty,
            1 => "1 页",
            _ => $"{pageCount} 页",
        };
        IsPreviewPending = false;
    }

    public void ShowPreviewUnavailable()
    {
        Thumbnail = null;
        DetailText = string.Empty;
        IsPreviewPending = false;
    }

    private static BitmapImage Load(string path)
    {
        var image = new BitmapImage();
        using (var stream = File.OpenRead(path))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = PreviewDecodeWidth;
            image.StreamSource = stream;
            image.EndInit();
        }

        image.Freeze();
        return image;
    }
}

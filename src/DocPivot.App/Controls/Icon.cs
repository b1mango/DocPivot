using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DocPivot.App.Controls;

/// <summary>
/// Strokes a 24 × 24 Lucide geometry at <see cref="Size"/> pixels in the inherited
/// foreground, so an icon always matches the text beside it. The pen stays 1.5 px
/// at every size, with round caps and joins as Lucide draws it.
/// </summary>
public sealed class Icon : FrameworkElement
{
    private const double GlyphBox = 24;
    private const double StrokePixels = 1.5;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data),
        typeof(Geometry),
        typeof(Icon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size),
        typeof(double),
        typeof(Icon),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon),
        new FrameworkPropertyMetadata(
            Brushes.Black,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    static Icon()
    {
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
        FocusableProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
        VerticalAlignmentProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(VerticalAlignment.Center));
    }

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Data is not { } data)
        {
            return;
        }

        var scale = Size / GlyphBox;
        var pen = new Pen(Foreground, StrokePixels / scale)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawGeometry(null, pen, data);
        drawingContext.Pop();
    }
}

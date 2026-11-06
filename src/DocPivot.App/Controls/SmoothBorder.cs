using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DocPivot.App.Controls;

/// <summary>
/// A <see cref="Border"/> whose rounded corners stay clean on any display scale. The stock border paints
/// its background inside the stroke, so the two anti-aliased edges meet on the curve and the stroke thins
/// out there, leaving the straight sides looking like corners that stick out. This one fills the whole
/// outline first and lays a device-pixel-wide stroke on top, so the line keeps one weight all the way
/// round. With <see cref="UIElement.ClipToBounds"/> set, the content is clipped to the rounded inside
/// instead of a square.
/// </summary>
public sealed class SmoothBorder : Border
{
    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        if (!TryGetUniform(size, out var thickness, out var radius))
        {
            base.OnRender(dc);
            return;
        }

        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        if (Background is { } background)
        {
            dc.DrawRoundedRectangle(background, null, new Rect(size), radius, radius);
        }

        var stroke = StrokeWidth(thickness);
        if (BorderBrush is { } brush && stroke > 0 && size.Width > stroke && size.Height > stroke)
        {
            var half = stroke / 2;
            var path = new Rect(half, half, size.Width - stroke, size.Height - stroke);
            var pathRadius = Math.Max(0, radius - half);
            dc.DrawRoundedRectangle(null, new Pen(brush, stroke), path, pathRadius, pathRadius);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        if (ClipToBounds && Child is { } child && TryGetUniform(arranged, out var thickness, out var radius))
        {
            child.Clip = InnerClip(child, arranged, StrokeWidth(thickness), radius);
        }

        return arranged;
    }

    private static RectangleGeometry InnerClip(UIElement child, Size size, double stroke, double radius)
    {
        var offset = VisualTreeHelper.GetOffset(child);
        var inner = new Rect(
            stroke - offset.X,
            stroke - offset.Y,
            Math.Max(0, size.Width - stroke * 2),
            Math.Max(0, size.Height - stroke * 2));
        var innerRadius = Math.Max(0, radius - stroke);
        var clip = new RectangleGeometry(inner, innerRadius, innerRadius);
        clip.Freeze();
        return clip;
    }

    /// <summary>Uniform stroke and corners are the only shapes this draws itself; anything else is left to <see cref="Border"/>.</summary>
    private bool TryGetUniform(Size size, out double thickness, out double radius)
    {
        var t = BorderThickness;
        var r = CornerRadius;
        thickness = t.Left;
        radius = Math.Min(r.TopLeft, Math.Min(size.Width, size.Height) / 2);
        return t.Left == t.Top && t.Left == t.Right && t.Left == t.Bottom
            && r.TopLeft == r.TopRight && r.TopLeft == r.BottomRight && r.TopLeft == r.BottomLeft;
    }

    /// <summary>Rounds the stroke to whole device pixels so straight sides stay crisp at 125% or 150% scale.</summary>
    private double StrokeWidth(double thickness)
    {
        if (thickness <= 0)
        {
            return 0;
        }

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return Math.Max(1, Math.Round(thickness * scale)) / scale;
    }
}

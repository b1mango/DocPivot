using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DocPivot.App.Controls;

/// <summary>
/// The drawing behind every <see cref="System.Windows.Controls.ProgressBar"/>: progress as a field of small
/// square dots, sparse and dim at the start and gathering into a dense, bright front at the reported value.
/// Each dot keeps deciding afresh whether it is lit, so the field twinkles while work is under way.
/// Indeterminate progress becomes the same front travelling across the full width. Dots are painted into a
/// bitmap at device-pixel resolution so they stay crisp squares at any scale; frames are only requested while
/// something is moving, and the field holds still when Windows animations are off.
/// </summary>
public sealed class DotMatrix : FrameworkElement
{
    private const double Pitch = 4;
    private const double Dot = 2;
    private const double GlideRate = 6;
    private const double FrameSeconds = 1d / 30;
    private const double RerollPerSecond = 2.4;
    private const double WaveSeconds = 2.4;
    private const double Spill = 2.5;

    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), 0d);
    public static readonly DependencyProperty MinimumProperty = Register(nameof(Minimum), 0d);
    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), 100d);
    public static readonly DependencyProperty IsIndeterminateProperty = Register(nameof(IsIndeterminate), false);
    public static readonly DependencyProperty FillProperty = Register<Brush?>(nameof(Fill), null);
    public static readonly DependencyProperty HotProperty = Register<Brush?>(nameof(Hot), null);
    public static readonly DependencyProperty TrackProperty = Register<Brush?>(nameof(Track), null);
    public static readonly DependencyProperty CornerRadiusProperty = Register(nameof(CornerRadius), 0d);
    public static readonly DependencyProperty InsetProperty = Register(nameof(Inset), 0d);
    public static readonly DependencyProperty IntensityProperty = Register(nameof(Intensity), 1d);

    private readonly Random _random = new();
    private WriteableBitmap? _bitmap;
    private int[] _pixels = [];
    private Cell[] _cells = [];
    private int _pixelWidth = -1;
    private int _pixelHeight = -1;
    private int _columns;
    private int _rows;
    private int _originX;
    private int _originY;
    private int _pitchPx;
    private int _dotPx;
    private double _scale = 1;
    private TimeSpan? _lastFrame;
    private double _sincePaint;
    private double _clock;
    private double _shown;
    private bool _subscribed;
    private Color _fill = Color.FromRgb(0xC9, 0x64, 0x42);
    private Color _hot = Colors.White;

    static DotMatrix()
    {
        IsHitTestVisibleProperty.OverrideMetadata(typeof(DotMatrix), new FrameworkPropertyMetadata(false));
        FocusableProperty.OverrideMetadata(typeof(DotMatrix), new FrameworkPropertyMetadata(false));
    }

    public DotMatrix()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        IsVisibleChanged += (_, _) => UpdateSubscription();
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => Unsubscribe();
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public bool IsIndeterminate { get => (bool)GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }

    /// <summary>The colour of the field; dots are this colour at partial strength.</summary>
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    /// <summary>The colour the brightest dots at the front run toward.</summary>
    public Brush? Hot { get => (Brush?)GetValue(HotProperty); set => SetValue(HotProperty, value); }

    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    /// <summary>Clear space between the track's edge and the outermost dots.</summary>
    public double Inset { get => (double)GetValue(InsetProperty); set => SetValue(InsetProperty, value); }

    /// <summary>Overall strength, 0–1; a quieter field sits behind text without competing with it.</summary>
    public double Intensity { get => (double)GetValue(IntensityProperty); set => SetValue(IntensityProperty, value); }

    private double Target
    {
        get
        {
            var span = Maximum - Minimum;
            return span <= 0 ? 0 : Math.Clamp((Value - Minimum) / span, 0, 1);
        }
    }

    private static bool MotionEnabled => SystemParameters.ClientAreaAnimation;

    private bool IsWorking => IsIndeterminate || Target < 1;

    private bool NeedsFrames => MotionEnabled && (IsWorking || Math.Abs(Target - _shown) > 0.0005);

    private static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(
            name,
            typeof(T),
            typeof(DotMatrix),
            new FrameworkPropertyMetadata(
                defaultValue,
                FrameworkPropertyMetadataOptions.AffectsRender,
                static (d, e) => ((DotMatrix)d).OnInputChanged(e)));

    private void OnInputChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.Property == FillProperty)
        {
            _fill = (e.NewValue as SolidColorBrush)?.Color ?? _fill;
        }
        else if (e.Property == HotProperty)
        {
            _hot = (e.NewValue as SolidColorBrush)?.Color ?? _hot;
        }
        else if (e.Property == CornerRadiusProperty || e.Property == InsetProperty)
        {
            _pixelWidth = -1;
        }

        var target = Target;
        if (!IsLoaded || !MotionEnabled || target < _shown || IsIndeterminate)
        {
            // Never glide backwards, and a row that appears mid-run starts where the work really is.
            _shown = target;
        }

        Paint();
        UpdateSubscription();
    }

    private void UpdateSubscription()
    {
        if (IsLoaded && IsVisible && NeedsFrames)
        {
            if (!_subscribed)
            {
                _subscribed = true;
                _lastFrame = null;
                CompositionTarget.Rendering += OnFrame;
            }
        }
        else
        {
            Unsubscribe();
        }
    }

    private void Unsubscribe()
    {
        if (_subscribed)
        {
            _subscribed = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_lastFrame == now)
        {
            return;
        }

        var dt = _lastFrame is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 0.1) : 0;
        _lastFrame = now;
        _clock += dt;
        _sincePaint += dt;

        var target = Target;
        _shown += (target - _shown) * (1 - Math.Exp(-dt * GlideRate));
        if (Math.Abs(target - _shown) < 0.0005)
        {
            _shown = target;
        }

        if (_sincePaint >= FrameSeconds)
        {
            Reroll(_sincePaint);
            _sincePaint = 0;
            Paint();
        }

        if (!NeedsFrames)
        {
            Unsubscribe();
        }
    }

    protected override Size MeasureOverride(Size availableSize) => default;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _pixelWidth = -1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        var radius = Math.Min(CornerRadius, Math.Min(size.Width, size.Height) / 2);
        if (Track is { } track)
        {
            drawingContext.DrawRoundedRectangle(track, null, new Rect(size), radius, radius);
        }

        EnsureGrid(size);
        if (_bitmap is not null)
        {
            drawingContext.DrawImage(_bitmap, new Rect(0, 0, _pixelWidth / _scale, _pixelHeight / _scale));
        }
    }

    /// <summary>Lays the dot grid out on whole device pixels, centred, keeping only dots wholly inside the rounded track.</summary>
    private void EnsureGrid(Size size)
    {
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var width = (int)Math.Round(size.Width * _scale);
        var height = (int)Math.Round(size.Height * _scale);
        if (width == _pixelWidth && height == _pixelHeight && _bitmap is not null)
        {
            return;
        }

        _pixelWidth = width;
        _pixelHeight = height;
        _pitchPx = Math.Max(3, (int)Math.Round(Pitch * _scale));
        _dotPx = Math.Clamp((int)Math.Round(Dot * _scale), 1, _pitchPx - 1);
        var inset = (int)Math.Round(Inset * _scale);
        var gap = _pitchPx - _dotPx;
        _columns = Math.Max(0, (width - 2 * inset + gap) / _pitchPx);
        _rows = Math.Max(0, (height - 2 * inset + gap) / _pitchPx);
        _originX = (width - (_columns * _pitchPx - gap)) / 2;
        _originY = (height - (_rows * _pitchPx - gap)) / 2;

        var radius = Math.Max(0, Math.Min(CornerRadius * _scale, Math.Min(width, height) / 2d) - inset);
        var bounds = new Rect(inset, inset, Math.Max(0, width - 2 * inset), Math.Max(0, height - 2 * inset));
        _cells = new Cell[_columns * _rows];
        for (var row = 0; row < _rows; row++)
        {
            for (var column = 0; column < _columns; column++)
            {
                var x = _originX + column * _pitchPx;
                var y = _originY + row * _pitchPx;
                _cells[row * _columns + column] = new Cell
                {
                    Inside = Inside(bounds, radius, x, y) && Inside(bounds, radius, x + _dotPx, y) &&
                        Inside(bounds, radius, x, y + _dotPx) && Inside(bounds, radius, x + _dotPx, y + _dotPx),
                    Seed = _random.NextSingle(),
                    Phase = _random.NextSingle() * MathF.Tau,
                    Speed = 3 + _random.NextSingle() * 5,
                };
            }
        }

        _bitmap = width > 0 && height > 0
            ? new WriteableBitmap(width, height, 96 * _scale, 96 * _scale, PixelFormats.Pbgra32, null)
            : null;
        _pixels = new int[Math.Max(0, width * height)];
        Paint();
    }

    private static bool Inside(Rect bounds, double radius, double x, double y)
    {
        var cx = Math.Clamp(x, bounds.Left + radius, Math.Max(bounds.Left + radius, bounds.Right - radius));
        var cy = Math.Clamp(y, bounds.Top + radius, Math.Max(bounds.Top + radius, bounds.Bottom - radius));
        var dx = x - cx;
        var dy = y - cy;
        return x >= bounds.Left && x <= bounds.Right && y >= bounds.Top && y <= bounds.Bottom &&
            dx * dx + dy * dy <= radius * radius + 0.25;
    }

    /// <summary>Each dot re-decides whether it is lit now and then; that is the twinkle.</summary>
    private void Reroll(double elapsed)
    {
        var chance = 1 - Math.Exp(-RerollPerSecond * elapsed);
        for (var i = 0; i < _cells.Length; i++)
        {
            if (_random.NextDouble() < chance)
            {
                _cells[i].Seed = _random.NextSingle();
            }
        }
    }

    private void Paint()
    {
        if (_bitmap is null || _pixels.Length == 0)
        {
            return;
        }

        Array.Clear(_pixels);
        var gridWidth = (double)(_columns * _pitchPx);
        var spill = Spill * _pitchPx;
        double head, tail;
        if (IsIndeterminate)
        {
            var span = Math.Max(90 * _scale, gridWidth * 0.45);
            var t = MotionEnabled ? _clock % WaveSeconds / WaveSeconds : 0.7;
            var eased = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
            head = _originX + eased * (gridWidth + span + spill);
            tail = head - span;
        }
        else
        {
            head = _originX + _shown * gridWidth;
            tail = _originX;
        }

        var twinkle = MotionEnabled && IsWorking;
        var intensity = Math.Clamp(Intensity, 0, 1);
        for (var column = 0; column < _columns; column++)
        {
            var x = _originX + column * _pitchPx + _dotPx / 2d;
            if (x < tail || x > head + spill)
            {
                continue;
            }

            double density, brightness, heat;
            if (x <= head)
            {
                // Sparse and dim at the start, gathering into a dense, bright front.
                var u = head - tail < 1 ? 1 : (x - tail) / (head - tail);
                density = 0.04 + 0.96 * Math.Pow(u, 1.6);
                brightness = 0.22 + 0.78 * Math.Pow(u, 1.5);
                heat = Math.Pow(Math.Clamp((u - 0.72) / 0.28, 0, 1), 1.6);
            }
            else
            {
                // A few stray dots just past the front keep it from reading as a hard edge.
                var t = (x - head) / spill;
                density = 0.4 * (1 - t);
                brightness = 0.85;
                heat = 0.6 * (1 - t);
            }

            for (var row = 0; row < _rows; row++)
            {
                var cell = _cells[row * _columns + column];
                if (!cell.Inside || cell.Seed >= density)
                {
                    continue;
                }

                var shimmer = twinkle ? 0.62 + 0.38 * Math.Sin(cell.Phase + _clock * cell.Speed) : 1;
                var alpha = brightness * shimmer * intensity;
                var color = Blend(_fill, _hot, heat * shimmer);
                FillDot(_originX + column * _pitchPx, _originY + row * _pitchPx, color, alpha);
            }
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, _pixelWidth, _pixelHeight), _pixels, _pixelWidth * 4, 0);
    }

    private void FillDot(int x, int y, Color color, double alpha)
    {
        var a = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
        if (a == 0)
        {
            return;
        }

        var pixel = (a << 24) | (color.R * a / 255 << 16) | (color.G * a / 255 << 8) | (color.B * a / 255);
        for (var dy = 0; dy < _dotPx; dy++)
        {
            Array.Fill(_pixels, pixel, (y + dy) * _pixelWidth + x, _dotPx);
        }
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));

    private struct Cell
    {
        public bool Inside;
        public float Seed;
        public float Phase;
        public float Speed;
    }
}

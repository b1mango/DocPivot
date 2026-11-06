using System.Windows;
using System.Windows.Media;

namespace DocPivot.App.Controls;

/// <summary>
/// The drawing behind every <see cref="System.Windows.Controls.ProgressBar"/>: the fill glides toward the
/// reported value instead of jumping, a soft light sweeps along it, and fine sparks drift off the leading
/// edge while work is under way. Indeterminate progress becomes a comet crossing the full rail. Frames are
/// only requested while something is moving, and motion is dropped when Windows animations are off.
/// </summary>
public sealed class ProgressFlow : FrameworkElement
{
    private const double GlideRate = 7.5;
    private const double SweepSeconds = 1.9;
    private const double CometSeconds = 1.7;
    private const double SparksPerSecond = 46;
    private const int MaxSparks = 90;

    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), 0d);
    public static readonly DependencyProperty MinimumProperty = Register(nameof(Minimum), 0d);
    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), 100d);
    public static readonly DependencyProperty IsIndeterminateProperty = Register(nameof(IsIndeterminate), false);
    public static readonly DependencyProperty FillProperty = Register<Brush?>(nameof(Fill), null);
    public static readonly DependencyProperty TrackProperty = Register<Brush?>(nameof(Track), null);
    public static readonly DependencyProperty RailThicknessProperty = Register(nameof(RailThickness), 4d);

    private readonly List<Spark> _sparks = [];
    private readonly Random _random = new();
    private TimeSpan? _lastFrame;
    private bool _subscribed;
    private double _shown;
    // Starts mid-cycle so a freshly shown indeterminate bar already has its comet on the rail.
    private double _clock = CometSeconds * 0.45;
    private double _emitDebt;
    private Color _accent = Color.FromRgb(0xC9, 0x64, 0x42);
    private Color[] _palette = [];
    private Brush? _fillBrush;

    static ProgressFlow()
    {
        IsHitTestVisibleProperty.OverrideMetadata(typeof(ProgressFlow), new FrameworkPropertyMetadata(false));
        FocusableProperty.OverrideMetadata(typeof(ProgressFlow), new FrameworkPropertyMetadata(false));
    }

    public ProgressFlow()
    {
        IsVisibleChanged += (_, _) => UpdateSubscription();
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => Unsubscribe();
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public bool IsIndeterminate { get => (bool)GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }

    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public double RailThickness { get => (double)GetValue(RailThicknessProperty); set => SetValue(RailThicknessProperty, value); }

    /// <summary>The fraction currently drawn; trails <see cref="Target"/> while gliding.</summary>
    internal double Shown => _shown;

    private double Target
    {
        get
        {
            var span = Maximum - Minimum;
            return span <= 0 ? 0 : Math.Clamp((Value - Minimum) / span, 0, 1);
        }
    }

    private static bool MotionEnabled => SystemParameters.ClientAreaAnimation;

    private bool IsWorking => IsIndeterminate || Target is > 0 and < 1;

    private bool NeedsFrames =>
        MotionEnabled && (IsWorking || _sparks.Count > 0 || Math.Abs(Target - _shown) > 0.0005);

    private static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(
            name,
            typeof(T),
            typeof(ProgressFlow),
            new FrameworkPropertyMetadata(
                defaultValue,
                FrameworkPropertyMetadataOptions.AffectsRender,
                static (d, e) => ((ProgressFlow)d).OnInputChanged(e)));

    private void OnInputChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.Property == FillProperty)
        {
            _accent = (e.NewValue as SolidColorBrush)?.Color ?? _accent;
            _palette = [];
            _fillBrush = null;
        }

        var target = Target;
        if (!MotionEnabled || target < _shown || IsIndeterminate)
        {
            // Never animate backwards: a new run or a reset starts the fill from where it really is.
            _shown = target;
        }

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

        var target = Target;
        _shown += (target - _shown) * (1 - Math.Exp(-dt * GlideRate));
        if (Math.Abs(target - _shown) < 0.0005)
        {
            _shown = target;
        }

        StepSparks(dt);
        InvalidateVisual();
        if (!NeedsFrames)
        {
            Unsubscribe();
        }
    }

    private void StepSparks(double dt)
    {
        for (var i = _sparks.Count - 1; i >= 0; i--)
        {
            var spark = _sparks[i];
            spark.Age += dt;
            if (spark.Age >= spark.Life)
            {
                _sparks.RemoveAt(i);
                continue;
            }

            spark.X += spark.Vx * dt;
            spark.Y += spark.Vy * dt;
            spark.Vy *= 1 - Math.Min(1, dt * 1.8);
            _sparks[i] = spark;
        }

        if (!IsWorking || ActualWidth <= 0)
        {
            _emitDebt = 0;
            return;
        }

        _emitDebt += dt * SparksPerSecond;
        var head = HeadX();
        var centerY = ActualHeight / 2;
        while (_emitDebt >= 1 && _sparks.Count < MaxSparks)
        {
            _emitDebt -= 1;
            _sparks.Add(new Spark
            {
                X = head - _random.NextDouble() * 6,
                Y = centerY + (_random.NextDouble() - 0.5) * RailThickness,
                Vx = -8 - _random.NextDouble() * 34,
                Vy = (_random.NextDouble() - 0.5) * 26,
                Life = 0.45 + _random.NextDouble() * 0.7,
                Size = 0.7 + _random.NextDouble() * 1.1,
                Hue = _random.Next(5),
            });
        }

        _emitDebt = Math.Min(_emitDebt, 1);
    }

    private double CometSpan => Math.Max(56, ActualWidth * 0.26);

    private double CometHead()
    {
        var t = _clock % CometSeconds / CometSeconds;
        var eased = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
        return -CometSpan + eased * (ActualWidth + CometSpan * 2);
    }

    private double HeadX() => IsIndeterminate ? CometHead() : _shown * ActualWidth;

    protected override Size MeasureOverride(Size availableSize) => new(0, RailThickness);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var thickness = Math.Min(RailThickness, ActualHeight);
        var radius = thickness / 2;
        var top = (ActualHeight - thickness) / 2;
        var rail = new Rect(0, top, width, thickness);
        drawingContext.DrawRoundedRectangle(Track, null, rail, radius, radius);

        var palette = Palette();
        var motion = MotionEnabled;
        if (IsIndeterminate)
        {
            drawingContext.PushClip(new RectangleGeometry(rail, radius, radius));
            var head = motion ? CometHead() : width * 0.62;
            var comet = new Rect(head - CometSpan, top, CometSpan, thickness);
            var brush = new LinearGradientBrush(
                [new GradientStop(WithAlpha(_accent, 0), 0), new GradientStop(_accent, 0.7), new GradientStop(palette[1], 1)],
                0);
            drawingContext.DrawRoundedRectangle(brush, null, comet, radius, radius);
            drawingContext.Pop();
            DrawHead(drawingContext, head, top + radius, palette);
        }
        else if (_shown > 0)
        {
            var fill = new Rect(0, top, Math.Max(thickness, _shown * width), thickness);
            drawingContext.DrawRoundedRectangle(FillBrush(palette), null, fill, radius, radius);
            if (motion && IsWorking)
            {
                DrawSweep(drawingContext, fill, radius);
                DrawHead(drawingContext, fill.Right, top + radius, palette);
            }
        }

        if (!motion)
        {
            return;
        }

        foreach (var spark in _sparks)
        {
            var life = spark.Age / spark.Life;
            var alpha = (byte)(230 * Math.Sin(Math.PI * Math.Min(1, life * 1.15)) * (1 - life * 0.35));
            var color = WithAlpha(palette[spark.Hue], alpha);
            drawingContext.DrawEllipse(new SolidColorBrush(color), null, new Point(spark.X, spark.Y), spark.Size, spark.Size);
        }
    }

    private void DrawSweep(DrawingContext drawingContext, Rect fill, double radius)
    {
        var span = Math.Max(40, ActualWidth * 0.18);
        var t = _clock % SweepSeconds / SweepSeconds;
        var x = -span + t * (fill.Width + span);
        var light = Colors.White;
        var sweep = new LinearGradientBrush(
            [new GradientStop(WithAlpha(light, 0), 0), new GradientStop(WithAlpha(light, 110), 0.5), new GradientStop(WithAlpha(light, 0), 1)],
            0);
        drawingContext.PushClip(new RectangleGeometry(fill, radius, radius));
        drawingContext.DrawRectangle(sweep, null, new Rect(x, fill.Top, span, fill.Height));
        drawingContext.Pop();
    }

    private void DrawHead(DrawingContext drawingContext, double x, double y, Color[] palette)
    {
        var pulse = 0.75 + 0.25 * Math.Sin(_clock * 5.2);
        var glow = new RadialGradientBrush(WithAlpha(palette[1], (byte)(150 * pulse)), WithAlpha(palette[1], 0));
        var r = RailThickness * 2.2;
        drawingContext.DrawEllipse(glow, null, new Point(x, y), r, r * 0.8);
        drawingContext.DrawEllipse(new SolidColorBrush(WithAlpha(Colors.White, (byte)(200 * pulse))), null, new Point(x - 0.5, y), 1.2, 1.2);
    }

    private Brush FillBrush(Color[] palette)
    {
        if (_fillBrush is null)
        {
            var brush = new LinearGradientBrush(
                [new GradientStop(Blend(_accent, Colors.Black, 0.08), 0), new GradientStop(_accent, 0.6), new GradientStop(palette[1], 1)],
                0);
            brush.Freeze();
            _fillBrush = brush;
        }

        return _fillBrush;
    }

    /// <summary>
    /// Sparks run through a warm spectrum anchored on the accent — clay, ember, amber, rose, lilac — so the
    /// effect reads as part of the palette rather than a rainbow pasted on top.
    /// </summary>
    private Color[] Palette()
    {
        if (_palette.Length == 0)
        {
            _palette =
            [
                _accent,
                Blend(_accent, Colors.White, 0.38),
                Blend(_accent, Color.FromRgb(0xF2, 0xB1, 0x4C), 0.7),
                Blend(_accent, Color.FromRgb(0xE8, 0x7A, 0x96), 0.65),
                Blend(_accent, Color.FromRgb(0xA9, 0x8B, 0xF0), 0.6),
            ];
        }

        return _palette;
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));

    private struct Spark
    {
        public double X;
        public double Y;
        public double Vx;
        public double Vy;
        public double Age;
        public double Life;
        public double Size;
        public int Hue;
    }
}

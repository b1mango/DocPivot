using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DocPivot.App.ViewModels;

namespace DocPivot.App.Services;

/// <summary>
/// Motion for the file queue lists.
/// <list type="bullet">
/// <item>Rows glide to their new slot when files are added, removed or moved
/// (a FLIP-style transition) and new rows fade in.</item>
/// <item>When <see cref="CanReorderProperty"/> is set, a row can be picked up
/// and dragged: it lifts and follows the pointer while the rows it passes
/// slide out of the way; on release it settles into the gap and the move is
/// committed to <see cref="WorkspaceViewModel.MoveFileTo"/>.</item>
/// </list>
/// </summary>
public static class ListMotion
{
    private static readonly Duration MoveDuration = new(TimeSpan.FromMilliseconds(180));
    private static readonly Duration LiftDuration = new(TimeSpan.FromMilliseconds(140));
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(ListMotion),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty CanReorderProperty =
        DependencyProperty.RegisterAttached(
            "CanReorder",
            typeof(bool),
            typeof(ListMotion),
            new PropertyMetadata(false));

    /// <summary>True on the row that is currently picked up (inherited by its template).</summary>
    public static readonly DependencyProperty IsLiftedProperty =
        DependencyProperty.RegisterAttached(
            "IsLifted",
            typeof(bool),
            typeof(ListMotion),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State",
            typeof(MotionState),
            typeof(ListMotion),
            new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static bool GetCanReorder(DependencyObject element) => (bool)element.GetValue(CanReorderProperty);

    public static void SetCanReorder(DependencyObject element, bool value) => element.SetValue(CanReorderProperty, value);

    public static bool GetIsLifted(DependencyObject element) => (bool)element.GetValue(IsLiftedProperty);

    public static void SetIsLifted(DependencyObject element, bool value) => element.SetValue(IsLiftedProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list)
        {
            return;
        }

        if (d.GetValue(StateProperty) is MotionState existing)
        {
            existing.Detach();
            d.ClearValue(StateProperty);
        }

        if (e.NewValue is true)
        {
            var state = new MotionState(list);
            d.SetValue(StateProperty, state);
            state.Attach();
        }
    }

    private static (ScaleTransform Scale, TranslateTransform Translate) EnsureTransforms(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup { Children: [ScaleTransform scale, TranslateTransform translate] })
        {
            return (scale, translate);
        }

        var group = new TransformGroup();
        var newScale = new ScaleTransform();
        var newTranslate = new TranslateTransform();
        group.Children.Add(newScale);
        group.Children.Add(newTranslate);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = group;
        return (newScale, newTranslate);
    }

    private static void Animate(Animatable target, DependencyProperty property, double to, Duration duration, EventHandler? completed = null)
    {
        var animation = new DoubleAnimation(to, duration) { EasingFunction = Ease };
        if (completed is not null)
        {
            animation.Completed += completed;
        }

        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void Snap(Animatable target, DependencyProperty property, double value)
    {
        target.BeginAnimation(property, null);
        target.SetValue(property, value);
    }

    private static T? FindAncestor<T>(DependencyObject? start)
        where T : DependencyObject
    {
        for (var current = start; current is not null; current = GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);

    private sealed class MotionState(ItemsControl list)
    {
        private readonly Dictionary<object, double> _visualTops = new(ReferenceEqualityComparer.Instance);
        private readonly List<object> _addedItems = [];
        private bool _layoutPending;
        private bool _suppressCollectionMotion;

        // Drag state
        private FrameworkElement? _pressed;
        private Point _pressPoint;
        private Drag? _drag;

        public void Attach()
        {
            ((INotifyCollectionChanged)list.Items).CollectionChanged += OnItemsChanged;
            list.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            list.PreviewMouseMove += OnPreviewMouseMove;
            list.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
            list.LostMouseCapture += OnLostMouseCapture;
        }

        public void Detach()
        {
            ((INotifyCollectionChanged)list.Items).CollectionChanged -= OnItemsChanged;
            list.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            list.PreviewMouseMove -= OnPreviewMouseMove;
            list.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
            list.LostMouseCapture -= OnLostMouseCapture;
            if (_layoutPending)
            {
                list.LayoutUpdated -= OnLayoutUpdated;
                _layoutPending = false;
            }
        }

        private Panel? ItemsHost =>
            list.Items.Count > 0 &&
            list.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement first
                ? VisualTreeHelper.GetParent(first) as Panel
                : null;

        // ---------- add / remove / move transitions ----------

        private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_suppressCollectionMotion || _drag is not null)
            {
                return;
            }

            if (e.Action == NotifyCollectionChangedAction.Reset || !list.IsVisible || !list.IsLoaded)
            {
                _visualTops.Clear();
                _addedItems.Clear();
                return;
            }

            // Snapshot where every surviving row is drawn right now (layout
            // offset plus any in-flight transition) before layout moves it.
            if (!_layoutPending)
            {
                _visualTops.Clear();
                foreach (var child in EnumerateContainers())
                {
                    var item = list.ItemContainerGenerator.ItemFromContainer(child);
                    if (item == DependencyProperty.UnsetValue || item is null)
                    {
                        continue;
                    }

                    var translateY = child.RenderTransform is TransformGroup { Children: [_, TranslateTransform t] } ? t.Y : 0;
                    _visualTops[item] = VisualTreeHelper.GetOffset(child).Y + translateY;
                }
            }

            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
            {
                foreach (var item in e.NewItems)
                {
                    _addedItems.Add(item);
                }
            }

            if (!_layoutPending)
            {
                _layoutPending = true;
                list.LayoutUpdated += OnLayoutUpdated;
            }
        }

        private IEnumerable<FrameworkElement> EnumerateContainers()
        {
            var host = ItemsHost;
            if (host is null)
            {
                yield break;
            }

            for (var index = 0; index < host.Children.Count; index++)
            {
                if (host.Children[index] is FrameworkElement child)
                {
                    yield return child;
                }
            }
        }

        private void OnLayoutUpdated(object? sender, EventArgs e)
        {
            list.LayoutUpdated -= OnLayoutUpdated;
            _layoutPending = false;

            var stagger = 0;
            foreach (var item in _addedItems)
            {
                if (list.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
                {
                    continue;
                }

                var (_, translate) = EnsureTransforms(container);
                var delay = TimeSpan.FromMilliseconds(Math.Min(stagger++, 8) * 18);
                container.Opacity = 0;
                Snap(translate, TranslateTransform.YProperty, 6);
                var fade = new DoubleAnimation(0, 1, MoveDuration) { BeginTime = delay, EasingFunction = Ease };
                fade.Completed += (_, _) =>
                {
                    container.BeginAnimation(UIElement.OpacityProperty, null);
                    container.Opacity = 1;
                };
                container.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
                translate.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(6, 0, MoveDuration) { BeginTime = delay, EasingFunction = Ease },
                    HandoffBehavior.SnapshotAndReplace);
            }

            foreach (var container in EnumerateContainers())
            {
                var item = list.ItemContainerGenerator.ItemFromContainer(container);
                if (item is null || !_visualTops.TryGetValue(item, out var oldTop) || _addedItems.Contains(item))
                {
                    continue;
                }

                var delta = oldTop - VisualTreeHelper.GetOffset(container).Y;
                if (Math.Abs(delta) < 0.5)
                {
                    continue;
                }

                var (_, translate) = EnsureTransforms(container);
                Snap(translate, TranslateTransform.YProperty, delta);
                Animate(translate, TranslateTransform.YProperty, 0, MoveDuration);
            }

            _visualTops.Clear();
            _addedItems.Clear();
        }

        // ---------- drag to reorder ----------

        private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _pressed = null;
            if (_drag is not null || !GetCanReorder(list) || e.OriginalSource is not DependencyObject source)
            {
                return;
            }

            for (var current = source; current is not null && !ReferenceEquals(current, list); current = GetParent(current))
            {
                if (current is ButtonBase or TextBoxBase or ComboBox or ScrollBar or Thumb or Hyperlink)
                {
                    return;
                }
            }

            if (ItemsControl.ContainerFromElement(list, source) is not FrameworkElement container ||
                ItemsHost is not { } host)
            {
                return;
            }

            _pressed = container;
            _pressPoint = e.GetPosition(host);
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_drag is { Settling: false } drag)
            {
                drag.Track(e.GetPosition(drag.Host));
                e.Handled = true;
                return;
            }

            if (_pressed is null || _drag is not null)
            {
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed || ItemsHost is not { } host)
            {
                _pressed = null;
                return;
            }

            var point = e.GetPosition(host);
            if (Math.Abs(point.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance &&
                Math.Abs(point.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance)
            {
                return;
            }

            _drag = Drag.TryBegin(this, list, host, _pressed, _pressPoint);
            _pressed = null;
            if (_drag is not null)
            {
                _drag.Track(point);
                e.Handled = true;
            }
        }

        private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _pressed = null;
            if (_drag is { Settling: false } drag)
            {
                e.Handled = true;
                drag.Release();
            }
        }

        private void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_drag is { Settling: false } drag)
            {
                drag.Release();
            }
        }

        private void Finish(Drag drag)
        {
            _drag = null;
            if (drag.TargetIndex == drag.FromIndex ||
                list.DataContext is not WorkspaceViewModel viewModel ||
                drag.Item is not QueuedFileViewModel file)
            {
                return;
            }

            // Every row is already drawn in its final slot; commit the move
            // without replaying a transition for it.
            _suppressCollectionMotion = true;
            try
            {
                var insertIndex = drag.TargetIndex > drag.FromIndex ? drag.TargetIndex + 1 : drag.TargetIndex;
                viewModel.MoveFileTo(file, insertIndex);
            }
            finally
            {
                _suppressCollectionMotion = false;
            }
        }

        private sealed class Drag
        {
            private readonly MotionState _owner;
            private readonly ItemsControl _list;
            private readonly FrameworkElement[] _rows;
            private readonly double[] _tops;
            private readonly double[] _heights;
            private readonly double[] _offsets;
            private readonly FrameworkElement _lifted;
            private readonly ScaleTransform _liftScale;
            private readonly TranslateTransform _liftTranslate;
            private readonly DropShadowEffect _shadow;
            private readonly Point _origin;
            private readonly ScrollViewer? _scroller;
            private readonly DispatcherTimer _autoScroll;

            private Drag(MotionState owner, ItemsControl list, Panel host, FrameworkElement[] rows, int fromIndex, Point origin)
            {
                _owner = owner;
                _list = list;
                Host = host;
                _rows = rows;
                FromIndex = fromIndex;
                TargetIndex = fromIndex;
                _origin = origin;
                _tops = new double[rows.Length];
                _heights = new double[rows.Length];
                _offsets = new double[rows.Length];
                for (var index = 0; index < rows.Length; index++)
                {
                    var (scale, translate) = EnsureTransforms(rows[index]);
                    Snap(translate, TranslateTransform.YProperty, 0);
                    Snap(scale, ScaleTransform.ScaleXProperty, 1);
                    Snap(scale, ScaleTransform.ScaleYProperty, 1);
                    rows[index].BeginAnimation(UIElement.OpacityProperty, null);
                    rows[index].Opacity = 1;
                    _tops[index] = VisualTreeHelper.GetOffset(rows[index]).Y;
                    _heights[index] = rows[index].ActualHeight;
                }

                _lifted = rows[fromIndex];
                Item = list.ItemContainerGenerator.ItemFromContainer(_lifted);
                (_liftScale, _liftTranslate) = EnsureTransforms(_lifted);
                _shadow = new DropShadowEffect
                {
                    BlurRadius = 22,
                    ShadowDepth = 6,
                    Direction = 270,
                    Color = Colors.Black,
                    Opacity = 0,
                    RenderingBias = RenderingBias.Performance,
                };
                _scroller = FindAncestor<ScrollViewer>(VisualTreeHelper.GetParent(list));
                _autoScroll = new DispatcherTimer(DispatcherPriority.Input, list.Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(16),
                };
                _autoScroll.Tick += OnAutoScroll;
            }

            public Panel Host { get; }

            public object? Item { get; }

            public int FromIndex { get; }

            public int TargetIndex { get; private set; }

            public bool Settling { get; private set; }

            public static Drag? TryBegin(MotionState owner, ItemsControl list, Panel host, FrameworkElement pressed, Point origin)
            {
                var rows = new FrameworkElement[list.Items.Count];
                var fromIndex = -1;
                for (var index = 0; index < rows.Length; index++)
                {
                    if (list.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement row)
                    {
                        return null;
                    }

                    rows[index] = row;
                    if (ReferenceEquals(row, pressed))
                    {
                        fromIndex = index;
                    }
                }

                if (fromIndex < 0 || rows.Length < 2 || !list.CaptureMouse())
                {
                    return null;
                }

                var drag = new Drag(owner, list, host, rows, fromIndex, origin);
                drag.Lift();
                return drag;
            }

            private void Lift()
            {
                Panel.SetZIndex(_lifted, 100);
                SetIsLifted(_lifted, true);
                _lifted.Effect = _shadow;
                _list.Cursor = Cursors.SizeNS;
                Animate(_liftScale, ScaleTransform.ScaleXProperty, 1.015, LiftDuration);
                Animate(_liftScale, ScaleTransform.ScaleYProperty, 1.015, LiftDuration);
                Animate(_shadow, DropShadowEffect.OpacityProperty, 0.16, LiftDuration);
                _autoScroll.Start();
            }

            public void Track(Point pointer)
            {
                var last = _rows.Length - 1;
                var minDy = -_tops[FromIndex];
                var maxDy = _tops[last] + _heights[last] - (_tops[FromIndex] + _heights[FromIndex]);
                var dy = Math.Clamp(pointer.Y - _origin.Y, minDy, maxDy);
                Snap(_liftTranslate, TranslateTransform.YProperty, dy);

                var center = _tops[FromIndex] + _heights[FromIndex] / 2 + dy;
                var target = FromIndex;
                for (var index = FromIndex + 1; index <= last && center > Mid(index); index++)
                {
                    target = index;
                }

                if (target == FromIndex)
                {
                    for (var index = FromIndex - 1; index >= 0 && center < Mid(index); index--)
                    {
                        target = index;
                    }
                }

                if (target == TargetIndex)
                {
                    return;
                }

                TargetIndex = target;
                var gap = _heights[FromIndex];
                for (var index = 0; index <= last; index++)
                {
                    if (index == FromIndex)
                    {
                        continue;
                    }

                    var desired =
                        index > FromIndex && index <= TargetIndex ? -gap :
                        index < FromIndex && index >= TargetIndex ? gap :
                        0;
                    if (desired.Equals(_offsets[index]))
                    {
                        continue;
                    }

                    _offsets[index] = desired;
                    var (_, translate) = EnsureTransforms(_rows[index]);
                    Animate(translate, TranslateTransform.YProperty, desired, MoveDuration);
                }
            }

            private double Mid(int index) => _tops[index] + _heights[index] / 2;

            private void OnAutoScroll(object? sender, EventArgs e)
            {
                if (_scroller is null || _scroller.ScrollableHeight <= 0)
                {
                    return;
                }

                const double edge = 40;
                var position = Mouse.GetPosition(_scroller);
                double step = 0;
                if (position.Y < edge)
                {
                    step = -Math.Ceiling((edge - Math.Max(position.Y, -edge)) / edge * 9);
                }
                else if (position.Y > _scroller.ViewportHeight - edge)
                {
                    step = Math.Ceiling((Math.Min(position.Y, _scroller.ViewportHeight + edge) - (_scroller.ViewportHeight - edge)) / edge * 9);
                }

                if (step == 0)
                {
                    return;
                }

                var before = _scroller.VerticalOffset;
                _scroller.ScrollToVerticalOffset(Math.Clamp(before + step, 0, _scroller.ScrollableHeight));
                _scroller.UpdateLayout();
                if (!_scroller.VerticalOffset.Equals(before))
                {
                    Track(Mouse.GetPosition(Host));
                }
            }

            public void Release()
            {
                if (Settling)
                {
                    return;
                }

                Settling = true;
                _autoScroll.Stop();
                _list.ClearValue(FrameworkElement.CursorProperty);
                if (_list.IsMouseCaptured)
                {
                    _list.ReleaseMouseCapture();
                }

                var finalTop =
                    TargetIndex > FromIndex ? _tops[TargetIndex] + _heights[TargetIndex] - _heights[FromIndex] :
                    TargetIndex < FromIndex ? _tops[TargetIndex] :
                    _tops[FromIndex];
                var finalDy = finalTop - _tops[FromIndex];
                Animate(_liftScale, ScaleTransform.ScaleXProperty, 1, MoveDuration);
                Animate(_liftScale, ScaleTransform.ScaleYProperty, 1, MoveDuration);
                Animate(_shadow, DropShadowEffect.OpacityProperty, 0, MoveDuration);
                Animate(_liftTranslate, TranslateTransform.YProperty, finalDy, MoveDuration, (_, _) => Complete());
            }

            private void Complete()
            {
                foreach (var row in _rows)
                {
                    var (scale, translate) = EnsureTransforms(row);
                    Snap(translate, TranslateTransform.YProperty, 0);
                    Snap(scale, ScaleTransform.ScaleXProperty, 1);
                    Snap(scale, ScaleTransform.ScaleYProperty, 1);
                }

                _lifted.Effect = null;
                _lifted.ClearValue(Panel.ZIndexProperty);
                _lifted.ClearValue(IsLiftedProperty);
                _owner.Finish(this);
            }
        }
    }
}

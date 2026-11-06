using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DocPivot.App.ViewModels;

namespace DocPivot.App.Services;

/// <summary>
/// Drag-to-reorder for a wrapping grid of equally sized cards (the PDF merge board). A card lifts and
/// follows the pointer in both directions; the cards it passes glide into their new slots, and on release
/// it settles into the gap before the move is committed to <see cref="WorkspaceViewModel.MoveFileTo"/>.
/// </summary>
public static class GridReorder
{
    private static readonly Duration MoveDuration = new(TimeSpan.FromMilliseconds(200));
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(GridReorder),
        new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(ReorderState),
        typeof(GridReorder));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list)
        {
            return;
        }

        if (list.GetValue(StateProperty) is ReorderState existing)
        {
            existing.Detach();
            list.ClearValue(StateProperty);
        }

        if (e.NewValue is true)
        {
            list.SetValue(StateProperty, new ReorderState(list));
        }
    }

    private static (ScaleTransform Scale, TranslateTransform Translate) EnsureTransforms(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup { Children: [ScaleTransform scale, TranslateTransform translate] })
        {
            return (scale, translate);
        }

        var group = new TransformGroup();
        scale = new ScaleTransform();
        translate = new TranslateTransform();
        group.Children.Add(scale);
        group.Children.Add(translate);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = group;
        return (scale, translate);
    }

    private static void Animate(Animatable target, DependencyProperty property, double to, EventHandler? completed = null)
    {
        var animation = new DoubleAnimation(to, MoveDuration) { EasingFunction = Ease };
        if (completed is not null)
        {
            animation.Completed += completed;
        }

        target.BeginAnimation(property, animation);
    }

    private static void Snap(Animatable target, DependencyProperty property, double value)
    {
        target.BeginAnimation(property, null);
        target.SetValue(property, value);
    }

    private static T? FindAncestor<T>(DependencyObject? start, DependencyObject stop)
        where T : DependencyObject
    {
        for (var current = start; current is not null && current != stop; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private sealed class ReorderState
    {
        private readonly ItemsControl _list;
        private FrameworkElement? _pressed;
        private Point _pressPoint;
        private Drag? _drag;

        public ReorderState(ItemsControl list)
        {
            _list = list;
            list.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            list.PreviewMouseMove += OnPreviewMouseMove;
            list.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
            list.LostMouseCapture += OnLostMouseCapture;
        }

        public void Detach()
        {
            _list.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            _list.PreviewMouseMove -= OnPreviewMouseMove;
            _list.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
            _list.LostMouseCapture -= OnLostMouseCapture;
        }

        private bool CanReorder => _list.DataContext is WorkspaceViewModel { CanReorderFiles: true };

        private Panel? ItemsHost =>
            _list.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement first
                ? VisualTreeHelper.GetParent(first) as Panel
                : null;

        private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _pressed = null;
            if (_drag is not null || !CanReorder || ItemsHost is not { } host ||
                e.OriginalSource is not DependencyObject source ||
                FindAncestor<ButtonBase>(source, _list) is not null)
            {
                return;
            }

            _pressed = ItemsControl.ContainerFromElement(_list, source) as FrameworkElement;
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

            _drag = Drag.TryBegin(this, _list, host, _pressed, _pressPoint);
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
            if (drag.TargetIndex != drag.FromIndex &&
                _list.DataContext is WorkspaceViewModel viewModel &&
                drag.Item is QueuedFileViewModel file)
            {
                var insertIndex = drag.TargetIndex > drag.FromIndex ? drag.TargetIndex + 1 : drag.TargetIndex;
                viewModel.MoveFileTo(file, insertIndex);
            }

            // The commit re-lays the cards out in the slots they are already drawn in, so the
            // offsets can be dropped in the same frame without a visible jump.
            drag.ResetTransforms();
        }

        private sealed class Drag
        {
            private readonly ReorderState _owner;
            private readonly ItemsControl _list;
            private readonly FrameworkElement[] _cards;
            private readonly Point[] _slots;
            private readonly Size _size;
            private readonly Point _origin;
            private readonly FrameworkElement _lifted;
            private readonly TranslateTransform _liftTranslate;

            private Drag(ReorderState owner, ItemsControl list, Panel host, FrameworkElement[] cards, int fromIndex, Point origin)
            {
                _owner = owner;
                _list = list;
                Host = host;
                _cards = cards;
                FromIndex = fromIndex;
                TargetIndex = fromIndex;
                _origin = origin;
                _slots = cards.Select(card => card.TranslatePoint(default, host)).ToArray();
                _lifted = cards[fromIndex];
                _size = new Size(_lifted.ActualWidth, _lifted.ActualHeight);
                Item = list.ItemContainerGenerator.ItemFromContainer(_lifted);

                foreach (var card in cards)
                {
                    var (scale, translate) = EnsureTransforms(card);
                    Snap(scale, ScaleTransform.ScaleXProperty, 1);
                    Snap(scale, ScaleTransform.ScaleYProperty, 1);
                    Snap(translate, TranslateTransform.XProperty, 0);
                    Snap(translate, TranslateTransform.YProperty, 0);
                }

                var (liftScale, liftTranslate) = EnsureTransforms(_lifted);
                _liftTranslate = liftTranslate;
                Panel.SetZIndex(_lifted, 100);
                _lifted.Opacity = 0.96;
                Animate(liftScale, ScaleTransform.ScaleXProperty, 1.04);
                Animate(liftScale, ScaleTransform.ScaleYProperty, 1.04);
                Mouse.OverrideCursor = Cursors.SizeAll;
            }

            public Panel Host { get; }

            public object Item { get; }

            public int FromIndex { get; }

            public int TargetIndex { get; private set; }

            public bool Settling { get; private set; }

            public static Drag? TryBegin(ReorderState owner, ItemsControl list, Panel host, FrameworkElement pressed, Point origin)
            {
                var cards = new FrameworkElement[list.Items.Count];
                var fromIndex = -1;
                for (var index = 0; index < cards.Length; index++)
                {
                    if (list.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement card)
                    {
                        return null;
                    }

                    cards[index] = card;
                    if (card == pressed)
                    {
                        fromIndex = index;
                    }
                }

                if (fromIndex < 0 || cards.Length < 2 || !list.CaptureMouse())
                {
                    return null;
                }

                return new Drag(owner, list, host, cards, fromIndex, origin);
            }

            public void Track(Point pointer)
            {
                var dx = pointer.X - _origin.X;
                var dy = pointer.Y - _origin.Y;
                Snap(_liftTranslate, TranslateTransform.XProperty, dx);
                Snap(_liftTranslate, TranslateTransform.YProperty, dy);

                var center = new Point(
                    _slots[FromIndex].X + _size.Width / 2 + dx,
                    _slots[FromIndex].Y + _size.Height / 2 + dy);
                var target = 0;
                var best = double.MaxValue;
                for (var index = 0; index < _slots.Length; index++)
                {
                    var slotCenter = new Point(_slots[index].X + _size.Width / 2, _slots[index].Y + _size.Height / 2);
                    var distance = (slotCenter - center).LengthSquared;
                    if (distance < best)
                    {
                        best = distance;
                        target = index;
                    }
                }

                if (target == TargetIndex)
                {
                    return;
                }

                TargetIndex = target;
                for (var index = 0; index < _cards.Length; index++)
                {
                    if (index == FromIndex)
                    {
                        continue;
                    }

                    var slot =
                        index > FromIndex && index <= TargetIndex ? index - 1 :
                        index < FromIndex && index >= TargetIndex ? index + 1 :
                        index;
                    var (_, translate) = EnsureTransforms(_cards[index]);
                    Animate(translate, TranslateTransform.XProperty, _slots[slot].X - _slots[index].X);
                    Animate(translate, TranslateTransform.YProperty, _slots[slot].Y - _slots[index].Y);
                }
            }

            public void Release()
            {
                Settling = true;
                Mouse.OverrideCursor = null;
                if (_list.IsMouseCaptured)
                {
                    _list.ReleaseMouseCapture();
                }

                var (liftScale, _) = EnsureTransforms(_lifted);
                Animate(liftScale, ScaleTransform.ScaleXProperty, 1);
                Animate(liftScale, ScaleTransform.ScaleYProperty, 1);
                Animate(_liftTranslate, TranslateTransform.XProperty, _slots[TargetIndex].X - _slots[FromIndex].X);
                Animate(
                    _liftTranslate,
                    TranslateTransform.YProperty,
                    _slots[TargetIndex].Y - _slots[FromIndex].Y,
                    (_, _) => _owner.Finish(this));
            }

            public void ResetTransforms()
            {
                foreach (var card in _cards)
                {
                    var (scale, translate) = EnsureTransforms(card);
                    Snap(scale, ScaleTransform.ScaleXProperty, 1);
                    Snap(scale, ScaleTransform.ScaleYProperty, 1);
                    Snap(translate, TranslateTransform.XProperty, 0);
                    Snap(translate, TranslateTransform.YProperty, 0);
                }

                _lifted.ClearValue(Panel.ZIndexProperty);
                _lifted.ClearValue(UIElement.OpacityProperty);
            }
        }
    }
}

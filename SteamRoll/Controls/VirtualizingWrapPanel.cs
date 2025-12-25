using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SteamRoll.Controls;

/// <summary>
/// A virtualizing panel that arranges children in a wrap layout.
/// Provides smooth scrolling and efficient memory usage for large item collections.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    #region Dependency Properties

    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(240.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(340.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    #endregion

    #region Private Fields

    private ScrollViewer? _scrollOwner;
    private Size _extent = new(0, 0);
    private Size _viewport = new(0, 0);
    private Point _offset = new(0, 0);
    private bool _canHorizontallyScroll;
    private bool _canVerticallyScroll = true;

    #endregion

    #region IScrollInfo Implementation

    public bool CanHorizontallyScroll
    {
        get => _canHorizontallyScroll;
        set => _canHorizontallyScroll = value;
    }

    public bool CanVerticallyScroll
    {
        get => _canVerticallyScroll;
        set => _canVerticallyScroll = value;
    }

    public double ExtentHeight => _extent.Height;
    public double ExtentWidth => _extent.Width;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public double ViewportHeight => _viewport.Height;
    public double ViewportWidth => _viewport.Width;

    public ScrollViewer? ScrollOwner
    {
        get => _scrollOwner;
        set => _scrollOwner = value;
    }

    public void LineUp() => SetVerticalOffset(_offset.Y - ItemHeight * 0.1);
    public void LineDown() => SetVerticalOffset(_offset.Y + ItemHeight * 0.1);
    public void LineLeft() => SetHorizontalOffset(_offset.X - ItemWidth * 0.1);
    public void LineRight() => SetHorizontalOffset(_offset.X + ItemWidth * 0.1);

    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width);
    public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width);

    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - ItemHeight * 0.5);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + ItemHeight * 0.5);
    public void MouseWheelLeft() => SetHorizontalOffset(_offset.X - ItemWidth * 0.5);
    public void MouseWheelRight() => SetHorizontalOffset(_offset.X + ItemWidth * 0.5);

    public void SetHorizontalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, _extent.Width - _viewport.Width));
        if (offset != _offset.X)
        {
            _offset.X = offset;
            InvalidateMeasure();
            _scrollOwner?.InvalidateScrollInfo();
        }
    }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, _extent.Height - _viewport.Height));
        if (offset != _offset.Y)
        {
            _offset.Y = offset;
            InvalidateMeasure();
            _scrollOwner?.InvalidateScrollInfo();
        }
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        // Find the item and scroll to it
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            if (InternalChildren[i] == visual)
            {
                // Calculate item position
                int itemsPerRow = CalculateItemsPerRow(_viewport.Width);
                int row = i / itemsPerRow;
                double targetY = row * ItemHeight;

                // Scroll if needed
                if (targetY < _offset.Y)
                    SetVerticalOffset(targetY);
                else if (targetY + ItemHeight > _offset.Y + _viewport.Height)
                    SetVerticalOffset(targetY + ItemHeight - _viewport.Height);

                break;
            }
        }
        return rectangle;
    }

    #endregion

    #region Layout Overrides

    protected override Size MeasureOverride(Size availableSize)
    {
        UpdateScrollInfo(availableSize);

        var generator = ItemContainerGenerator;
        if (generator == null)
            return availableSize;

        var itemsControl = ItemsControl.GetItemsOwner(this);
        if (itemsControl == null)
            return availableSize;

        int itemCount = itemsControl.Items.Count;
        if (itemCount == 0)
        {
            // Clean up any remaining containers
            CleanUpItems(0, 0);
            return availableSize;
        }

        int itemsPerRow = CalculateItemsPerRow(availableSize.Width);
        int firstVisibleRow = (int)Math.Floor(_offset.Y / ItemHeight);
        int lastVisibleRow = (int)Math.Ceiling((_offset.Y + availableSize.Height) / ItemHeight);

        // Add buffer rows for smoother scrolling
        firstVisibleRow = Math.Max(0, firstVisibleRow - 1);
        lastVisibleRow = Math.Min((itemCount - 1) / itemsPerRow, lastVisibleRow + 1);

        int firstVisibleIndex = firstVisibleRow * itemsPerRow;
        int lastVisibleIndex = Math.Min(itemCount - 1, (lastVisibleRow + 1) * itemsPerRow - 1);

        // Generate visible items
        using (generator.StartAt(generator.GeneratorPositionFromIndex(firstVisibleIndex), GeneratorDirection.Forward, true))
        {
            for (int i = firstVisibleIndex; i <= lastVisibleIndex; i++)
            {
                var child = generator.GenerateNext(out bool isNewlyRealized) as UIElement;
                if (child == null) continue;

                if (isNewlyRealized)
                {
                    InsertInternalChild(InternalChildren.Count, child);
                    generator.PrepareItemContainer(child);
                }

                child.Measure(new Size(ItemWidth, ItemHeight));
            }
        }

        // Clean up items outside visible range
        CleanUpItems(firstVisibleIndex, lastVisibleIndex);

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        if (generator == null)
            return finalSize;

        int itemsPerRow = CalculateItemsPerRow(finalSize.Width);
        double startX = (finalSize.Width - (itemsPerRow * ItemWidth)) / 2; // Center items

        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            int itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));

            if (itemIndex >= 0)
            {
                int row = itemIndex / itemsPerRow;
                int col = itemIndex % itemsPerRow;

                double x = startX + (col * ItemWidth);
                double y = (row * ItemHeight) - _offset.Y;

                child.Arrange(new Rect(x, y, ItemWidth, ItemHeight));
            }
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                RemoveInternalChildRange(0, InternalChildren.Count);
                break;
        }

        base.OnItemsChanged(sender, args);
    }

    #endregion

    #region Helper Methods

    private int CalculateItemsPerRow(double availableWidth)
    {
        int itemsPerRow = (int)Math.Floor(availableWidth / ItemWidth);
        return Math.Max(1, itemsPerRow);
    }

    private void UpdateScrollInfo(Size availableSize)
    {
        var itemsControl = ItemsControl.GetItemsOwner(this);
        int itemCount = itemsControl?.Items.Count ?? 0;

        if (itemCount == 0 || double.IsInfinity(availableSize.Width))
        {
            _extent = new Size(0, 0);
            _viewport = availableSize;
            _scrollOwner?.InvalidateScrollInfo();
            return;
        }

        int itemsPerRow = CalculateItemsPerRow(availableSize.Width);
        int rows = (int)Math.Ceiling((double)itemCount / itemsPerRow);

        var newExtent = new Size(availableSize.Width, rows * ItemHeight);
        var newViewport = availableSize;

        if (newExtent != _extent || newViewport != _viewport)
        {
            _extent = newExtent;
            _viewport = newViewport;

            // Clamp offset if extent shrunk
            if (_offset.Y > _extent.Height - _viewport.Height)
                _offset.Y = Math.Max(0, _extent.Height - _viewport.Height);

            _scrollOwner?.InvalidateScrollInfo();
        }
    }

    private void CleanUpItems(int firstVisibleIndex, int lastVisibleIndex)
    {
        var generator = ItemContainerGenerator;
        if (generator == null) return;

        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            int itemIndex = generator.IndexFromGeneratorPosition(position);

            if (itemIndex < firstVisibleIndex || itemIndex > lastVisibleIndex)
            {
                generator.Remove(position, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    #endregion
}

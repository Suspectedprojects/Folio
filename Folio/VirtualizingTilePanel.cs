using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Folio;

/// <summary>A wrapping grid of equal-size tiles that only creates the tiles on screen,
/// so folders with tens of thousands of files stay fast at any thumbnail size.</summary>
public class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(120.0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnTileSizeChanged));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(140.0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnTileSizeChanged));

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    Size _extent, _viewport;
    double _offsetY;
    int _cols = 1;
    int _anchor = -1; // first visible item to keep in view while resizing tiles

    static void OnTileSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var p = (VirtualizingTilePanel)d;
        if (p._anchor >= 0) return;
        double oldH = e.Property == ItemHeightProperty ? (double)e.OldValue : p.ItemHeight;
        if (oldH > 0) p._anchor = (int)((p._offsetY + oldH * 0.5) / oldH) * p._cols;
    }

    int ItemCount => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? ItemWidth * 4 : available.Width;
        double h = double.IsInfinity(available.Height) ? ItemHeight * 3 : available.Height;
        int count = ItemCount;
        _cols = Math.Max(1, (int)(w / ItemWidth));
        int rows = (count + _cols - 1) / _cols;

        if (_anchor >= 0) { _offsetY = (_anchor / _cols) * ItemHeight; _anchor = -1; }
        var extent = new Size(w, rows * ItemHeight);
        var viewport = new Size(w, h);
        _offsetY = Math.Max(0, Math.Min(_offsetY, extent.Height - viewport.Height));
        _extent = extent;
        _viewport = viewport;
        ScrollOwner?.InvalidateScrollInfo();

        int first = 0, last = -1;
        if (count > 0)
        {
            // one extra row above and below so arrow-key navigation always has a neighbour
            int firstRow = Math.Max(0, (int)Math.Floor(_offsetY / ItemHeight) - 1);
            int lastRow = Math.Min(rows - 1, (int)Math.Floor((_offsetY + h) / ItemHeight) + 1);
            first = firstRow * _cols;
            last = Math.Min(count - 1, (lastRow + 1) * _cols - 1);
        }

        var children = InternalChildren; // touching this first makes sure the generator exists
        var gen = ItemContainerGenerator;
        if (last >= first)
        {
            var start = gen.GeneratorPositionFromIndex(first);
            int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (gen.StartAt(start, GeneratorDirection.Forward, true))
            {
                for (int i = first; i <= last; i++, childIndex++)
                {
                    var child = (UIElement)gen.GenerateNext(out bool isNew);
                    if (isNew)
                    {
                        if (childIndex >= children.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        gen.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(ItemWidth, ItemHeight));
                }
            }
        }
        CleanUp(first, last);
        return viewport;
    }

    void CleanUp(int first, int last)
    {
        var children = InternalChildren;
        var gen = ItemContainerGenerator;
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var pos = new GeneratorPosition(i, 0);
            int idx = gen.IndexFromGeneratorPosition(pos);
            if (idx >= first && idx <= last) continue;
            if (children[i].IsKeyboardFocusWithin && idx >= 0) continue; // keep focus alive
            gen.Remove(pos, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var gen = ItemContainerGenerator;
        var children = InternalChildren;
        for (int i = 0; i < children.Count; i++)
        {
            int idx = gen.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (idx < 0) continue;
            int r = idx / _cols, c = idx % _cols;
            children[i].Arrange(new Rect(c * ItemWidth, r * ItemHeight - _offsetY, ItemWidth, ItemHeight));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
        }
        base.OnItemsChanged(sender, args);
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0) return;
        double top = (index / Math.Max(1, _cols)) * ItemHeight;
        if (top < _offsetY) SetVerticalOffset(top);
        else if (top + ItemHeight > _offsetY + _viewport.Height) SetVerticalOffset(top + ItemHeight - _viewport.Height);
    }

    // ---- IScrollInfo ----
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offsetY;
    public ScrollViewer? ScrollOwner { get; set; }

    double WheelStep => Math.Clamp(ItemHeight * 0.5, 60, 400);
    public void LineUp() => SetVerticalOffset(_offsetY - 40);
    public void LineDown() => SetVerticalOffset(_offsetY + 40);
    public void PageUp() => SetVerticalOffset(_offsetY - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offsetY + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offsetY - WheelStep);
    public void MouseWheelDown() => SetVerticalOffset(_offsetY + WheelStep);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, _extent.Height - _viewport.Height));
        if (offset == _offsetY) return;
        _offsetY = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        DependencyObject? d = visual;
        while (d != null && VisualTreeHelper.GetParent(d) != this) d = VisualTreeHelper.GetParent(d);
        if (d is UIElement child)
        {
            int i = InternalChildren.IndexOf(child);
            if (i >= 0) BringIndexIntoView(ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0)));
        }
        return rectangle;
    }
}

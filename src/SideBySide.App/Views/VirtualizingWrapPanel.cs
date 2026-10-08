using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SideBySide.App.Views;

/// <summary>
/// 같은 크기 항목을 줄 바꿈 배치하면서 화면에 보이는 줄(+위아래 한 줄)만 컨테이너를 만드는 패널.
/// WPF 기본 WrapPanel은 가상화가 없어 항목이 많은 폴더에서 아이콘 보기가 느려지므로 직접 구현했다.
/// 세로 스크롤만 지원한다.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel), new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel), new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private Size _extent;
    private Size _viewport;
    private Point _offset;

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

    private int ItemCount => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

    private int ItemsPerRow => Math.Max(1, (int)Math.Floor(_viewport.Width / ItemWidth));

    /// <summary>
    /// 패널 내용 좌표(스크롤 오프셋 포함) 사각형과 겹치는 항목 인덱스. 화면 밖(가상화로 컨테이너가 없는) 항목도 계산으로 포함한다.
    /// 마우스 끌기 영역 선택에 쓰인다.
    /// </summary>
    public IEnumerable<int> IndicesIntersecting(Rect rect, int count)
    {
        var perRow = ItemsPerRow;
        if (rect.Right < 0 || rect.Bottom < 0 || rect.Left >= perRow * ItemWidth) yield break;
        var colFirst = Math.Max(0, (int)Math.Floor(rect.Left / ItemWidth));
        var colLast = Math.Min(perRow - 1, (int)Math.Floor(Math.Max(0, rect.Right - 0.01) / ItemWidth));
        var rowFirst = Math.Max(0, (int)Math.Floor(rect.Top / ItemHeight));
        var rowLast = (int)Math.Floor(Math.Max(0, rect.Bottom - 0.01) / ItemHeight);
        for (int row = rowFirst; row <= rowLast; row++)
        {
            for (int col = colFirst; col <= colLast; col++)
            {
                var index = (row * perRow) + col;
                if (index >= count) yield break;
                yield return index;
            }
        }
    }

    // ── 배치 ────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        // InternalChildren을 먼저 읽어야 ItemContainerGenerator가 초기화된다 (WPF 가상화 패널 규칙)
        _ = InternalChildren;
        var viewport = new Size(
            double.IsInfinity(availableSize.Width) ? ItemWidth * 4 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? ItemHeight * 4 : availableSize.Height);

        var count = ItemCount;
        var perRow = Math.Max(1, (int)Math.Floor(viewport.Width / ItemWidth));
        var rows = (count + perRow - 1) / perRow;
        UpdateScrollInfo(viewport, new Size(perRow * ItemWidth, rows * ItemHeight));

        if (count == 0)
        {
            CleanUp(0, -1);
            return viewport;
        }

        var firstRow = Math.Max(0, (int)Math.Floor(_offset.Y / ItemHeight) - 1);
        var lastRow = (int)Math.Ceiling((_offset.Y + viewport.Height) / ItemHeight);
        var first = Math.Min(count - 1, firstRow * perRow);
        var last = Math.Min(count - 1, ((lastRow + 1) * perRow) - 1);

        var generator = ItemContainerGenerator;
        var start = generator.GeneratorPositionFromIndex(first);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;

        using (generator.StartAt(start, GeneratorDirection.Forward, true))
        {
            for (int i = first; i <= last; i++, childIndex++)
            {
                if (generator.GenerateNext(out var newlyRealized) is not UIElement child) break;
                if (newlyRealized)
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }

                child.Measure(new Size(ItemWidth, ItemHeight));
            }
        }

        CleanUp(first, last);
        return viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _ = InternalChildren;
        var generator = ItemContainerGenerator;
        if (generator is null) return finalSize;
        var perRow = ItemsPerRow;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0) continue;
            var row = index / perRow;
            var col = index % perRow;
            InternalChildren[i].Arrange(new Rect(col * ItemWidth, (row * ItemHeight) - _offset.Y, ItemWidth, ItemHeight));
        }

        return finalSize;
    }

    private void CleanUp(int first, int last)
    {
        var generator = ItemContainerGenerator;
        if (generator is null) return;
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last) continue;

            // 포커스를 가진 컨테이너는 남겨둔다 (키보드 탐색, 이름 편집 중 사라지지 않도록)
            if (InternalChildren[i].IsKeyboardFocusWithin) continue;
            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                _offset = default;
                ScrollOwner?.InvalidateScrollInfo();
                break;
        }

        base.OnItemsChanged(sender, args);
    }

    protected override void OnClearChildren()
    {
        base.OnClearChildren();
        RemoveInternalChildRange(0, InternalChildren.Count);
    }

    protected override void BringIndexIntoView(int index)
    {
        var row = index / ItemsPerRow;
        var top = row * ItemHeight;
        if (top < _offset.Y) SetVerticalOffset(top);
        else if (top + ItemHeight > _offset.Y + _viewport.Height) SetVerticalOffset(top + ItemHeight - _viewport.Height);
    }

    // ── IScrollInfo ─────────────────────────────────────────────────

    public bool CanHorizontallyScroll { get; set; }

    public bool CanVerticallyScroll { get; set; }

    public double ExtentWidth => _extent.Width;

    public double ExtentHeight => _extent.Height;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => 0;

    public double VerticalOffset => _offset.Y;

    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(_offset.Y - (ItemHeight / 3));

    public void LineDown() => SetVerticalOffset(_offset.Y + (ItemHeight / 3));

    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);

    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);

    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - (ItemHeight * 0.75));

    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + (ItemHeight * 0.75));

    public void LineLeft()
    {
    }

    public void LineRight()
    {
    }

    public void PageLeft()
    {
    }

    public void PageRight()
    {
    }

    public void MouseWheelLeft()
    {
    }

    public void MouseWheelRight()
    {
    }

    public void SetHorizontalOffset(double offset)
    {
    }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, _extent.Height - _viewport.Height));
        if (Math.Abs(offset - _offset.Y) < 0.5) return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            if (!ReferenceEquals(InternalChildren[i], visual) && !InternalChildren[i].IsAncestorOf(visual)) continue;
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index >= 0) BringIndexIntoView(index);
            break;
        }

        return rectangle;
    }

    private void UpdateScrollInfo(Size viewport, Size extent)
    {
        if (viewport == _viewport && extent == _extent) return;
        _viewport = viewport;
        _extent = extent;
        _offset.Y = Math.Max(0, Math.Min(_offset.Y, _extent.Height - _viewport.Height));
        ScrollOwner?.InvalidateScrollInfo();
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using SideBySide.App.Commands;
using SideBySide.App.Infrastructure;
using SideBySide.App.ViewModels;
using SideBySide.Core.FileSystem;
using SideBySide.Core.Settings;
using SideBySide.Core.Localization;

namespace SideBySide.App.Views;

/// <summary>
/// 탭 + 탐색 줄 + 파일 목록으로 된 창 하나. DataContext = <see cref="PaneViewModel"/>.
/// 코드 비하인드는 ListView의 마우스/드래그/선택 동기화처럼 MVVM으로 표현하기 어려운 UI 동작만 처리하고
/// 실제 동작은 <see cref="MainViewModel"/>에 위임한다.
/// </summary>
public partial class FilePane : UserControl
{
    private PaneViewModel? _pane;
    private TabViewModel? _tab;
    private ScrollViewer? _scroll;
    private Point _dragStart;
    private FileItemViewModel? _pressedItem;
    private bool _deferSelect;
    private bool _keysBound;
    private MainViewModel? _main;
    private readonly GridView _gridView;
    private readonly GridViewColumn _locationColumn;
    private ViewMode _appliedView = ViewMode.Details;

    // 마우스 끌기 영역 선택 상태
    private bool _marqueePending;
    private bool _marqueeActive;
    private Point _marqueeStart;            // 패널 내용 좌표 (스크롤해도 시작점이 고정되도록)
    private HashSet<FileItemViewModel> _marqueeBase = [];
    private HashSet<FileItemViewModel> _marqueeLast = [];
    private readonly System.Windows.Threading.DispatcherTimer _autoScroll;

    public FilePane()
    {
        InitializeComponent();
        _gridView = (GridView)List.View;
        _locationColumn = new GridViewColumn
        {
            Header = new GridViewColumnHeader { Content = L.T("위치") },
            Width = 200,
            CellTemplate = (DataTemplate)FindResource("Cell.Location"),
        };
        DataContextChanged += (_, _) => AttachPane(DataContext as PaneViewModel);
        _autoScroll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _autoScroll.Tick += (_, _) => AutoScrollMarquee();
        Loaded += (_, _) => EnsureHooks();
        IsVisibleChanged += (_, _) => EnsureHooks();
    }

    public MainViewModel? Main
    {
        get => _main;
        set
        {
            if (_main is not null) _main.PropertyChanged -= OnMainPropertyChanged;
            _main = value;
            if (_main is not null) _main.PropertyChanged += OnMainPropertyChanged;
            ApplyView();
        }
    }

    /// <summary>세로 스크롤 위치 변경 (비교 모드 좌우 동기화용).</summary>
    public event EventHandler<double>? VerticalScrolled;

    // ── 외부 API ────────────────────────────────────────────────────

    public void FocusList()
    {
        if (List.SelectedItem is { } item && List.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem container)
            container.Focus();
        else
            List.Focus();
    }

    public void FocusAddress() => Address.BeginEdit();

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void ScrollToVerticalOffset(double offset) => _scroll?.ScrollToVerticalOffset(offset);

    public void ShowContextMenuForSelection()
    {
        if (Main is null || _pane is null) return;
        FrameworkElement anchor = List;
        var origin = new Point(16, 16);
        if (List.SelectedItem is { } item && List.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem container)
        {
            anchor = container;
            origin = new Point(24, container.ActualHeight);
        }

        var screen = anchor.PointToScreen(origin);
        Main.ShowContextMenu(_pane, (int)screen.X, (int)screen.Y, onBackground: List.SelectedItems.Count == 0, extended: false);
    }

    // ── 연결 ────────────────────────────────────────────────────────

    /// <summary>단축키와 스크롤 감시 연결. 숨겨진 채 시작한 창은 템플릿이 늦게 적용되므로 보일 때 다시 시도한다.</summary>
    private void EnsureHooks()
    {
        if (!_keysBound && CommandHost.Current is { } host)
        {
            host.BindKeys(List, CommandScope.FileList);
            _keysBound = true;
        }

        if (_scroll is not null || !IsLoaded) return;
        _scroll = VisualTree.FindDescendant<ScrollViewer>(List);
        if (_scroll is not null)
        {
            _scroll.ScrollChanged += (_, args) =>
            {
                if (args.VerticalChange != 0) VerticalScrolled?.Invoke(this, args.VerticalOffset);
            };
        }
    }

    private void AttachPane(PaneViewModel? pane)
    {
        if (_pane is not null) _pane.PropertyChanged -= OnPanePropertyChanged;
        _pane = pane;
        if (_pane is not null) _pane.PropertyChanged += OnPanePropertyChanged;
        AttachTab(_pane?.ActiveTab);
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.ActiveTab)) AttachTab(_pane?.ActiveTab);
    }

    private void AttachTab(TabViewModel? tab)
    {
        if (_tab is not null)
        {
            _tab.SelectRequested -= OnSelectRequested;
            _tab.PropertyChanged -= OnTabPropertyChanged;
        }

        _tab = tab;
        if (_tab is not null)
        {
            _tab.SelectRequested += OnSelectRequested;
            _tab.PropertyChanged += OnTabPropertyChanged;
        }

        UpdateSortGlyphs();
        UpdateLocationColumn();
        ApplyView();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.SortBy) or nameof(TabViewModel.SortDescending))
            UpdateSortGlyphs();
        else if (e.PropertyName is nameof(TabViewModel.IsRecursiveSearch))
            UpdateLocationColumn();
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ViewMode) or nameof(MainViewModel.IsDualPane) or nameof(MainViewModel.IsCompareMode)) ApplyView();
    }

    /// <summary>자세히 / 아이콘 보기 전환. 비교 모드는 행 정렬이 필요하므로 항상 자세히 보기.</summary>
    private void ApplyView()
    {
        var mode = _main is null || _main.IsCompareMode || _tab?.IsCompareView == true ? ViewMode.Details : _main.ViewMode;
        if (mode == _appliedView) return;
        _appliedView = mode;

        if (mode == ViewMode.Details)
        {
            List.ItemTemplate = null;
            List.ClearValue(ItemsControl.ItemsPanelProperty);
            List.ItemContainerStyle = (Style)FindResource("ListViewItem.File");
            List.View = _gridView;
        }
        else
        {
            var large = mode == ViewMode.LargeIcons;
            List.View = null;
            List.ItemsPanel = (ItemsPanelTemplate)FindResource(large ? "Panel.LargeIcons" : "Panel.MediumIcons");
            List.ItemTemplate = (DataTemplate)FindResource(large ? "Tile.Large" : "Tile.Medium");
            List.ItemContainerStyle = (Style)FindResource("ListViewItem.Tile");
        }

        // 템플릿이 바뀌면 내부 ScrollViewer도 새로 만들어지므로 다시 연결
        _scroll = null;
        _itemsHost = null;
        Dispatcher.BeginInvoke(EnsureHooks, DispatcherPriority.Loaded);
        if (List.SelectedItem is { } selected) Dispatcher.BeginInvoke(() => List.ScrollIntoView(selected), DispatcherPriority.Loaded);
    }

    private void UpdateLocationColumn()
    {
        var show = _tab?.IsRecursiveSearch == true;
        var present = _gridView.Columns.Contains(_locationColumn);
        if (show && !present) _gridView.Columns.Insert(1, _locationColumn);
        else if (!show && present) _gridView.Columns.Remove(_locationColumn);
    }

    private void OnListMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || _main is null) return;
        _main.CycleViewMode(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void UpdateSortGlyphs()
    {
        foreach (var glyph in new[] { SortName, SortModified, SortType, SortSize }) glyph.Text = string.Empty;
        if (_tab is null || _tab.IsCompareView) return;
        var target = _tab.SortBy switch
        {
            SortColumn.Modified => SortModified,
            SortColumn.Type => SortType,
            SortColumn.Size => SortSize,
            _ => SortName,
        };
        target.Text = _tab.SortDescending ? "" : "";
    }

    private void Activate() => Main?.Activate(_pane!);

    private void OnAnyMouseDown(object sender, MouseButtonEventArgs e) => Activate();

    private void OnAnyKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => Activate();

    // ── 선택 ────────────────────────────────────────────────────────

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        => _tab?.SetSelection(List.SelectedItems.Cast<FileItemViewModel>().ToList());

    private void OnSelectRequested(object? sender, SelectRequest request)
        => Dispatcher.BeginInvoke(() => ApplySelection(request), DispatcherPriority.Loaded);

    private void ApplySelection(SelectRequest request)
    {
        if (_tab is null) return;
        var items = _tab.Items;
        List<FileItemViewModel> targets;
        if (request.Indices is not null)
        {
            targets = request.Indices.Where(i => i >= 0 && i < items.Count).Select(i => items[i]).ToList();
        }
        else
        {
            var names = new HashSet<string>(request.Names, StringComparer.OrdinalIgnoreCase);
            targets = items.Where(i => names.Contains(i.Name) && !i.IsPlaceholder).ToList();
            if (targets.Count == 0) targets = items.Where(i => names.Contains(i.Name)).ToList();
        }

        var hadFocus = List.IsKeyboardFocusWithin;
        if (targets.Count == items.Count && items.Count > 0) List.SelectAll();
        else List.SelectItems(targets);
        if (targets.Count == 0) return;

        List.ScrollIntoView(targets[0]);
        if (request.BeginRename)
        {
            targets[0].IsEditing = true;
            return;
        }

        if (hadFocus && !items.Any(i => i.IsEditing))
        {
            List.UpdateLayout();
            (List.ItemContainerGenerator.ContainerFromItem(targets[0]) as ListViewItem)?.Focus();
        }
    }

    // ── 정렬 ────────────────────────────────────────────────────────

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Tag: string tag } && Enum.TryParse<SortColumn>(tag, out var column))
            _tab?.Sort(column);
    }

    // ── 탐색 버튼 ───────────────────────────────────────────────────

    private void OnBack(object sender, RoutedEventArgs e) => _ = _tab?.GoBackAsync();

    private void OnForward(object sender, RoutedEventArgs e) => _ = _tab?.GoForwardAsync();

    private void OnUp(object sender, RoutedEventArgs e) => _ = _tab?.GoUpAsync();

    private void OnRefresh(object sender, RoutedEventArgs e) => Main?.Commands.Execute(CommandIds.Refresh);

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_tab is not null) _tab.SearchText = string.Empty;
            FocusList();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _tab is { SearchSubfolders: false } tab && tab.SearchText.Trim().Length > 0)
        {
            // Enter: 하위 폴더까지 검색 (검색란에 그대로 머무름)
            tab.SearchSubfolders = true;
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Down)
        {
            if (List.Items.Count > 0 && List.SelectedItems.Count == 0) List.SelectedIndex = 0;
            FocusList();
            e.Handled = true;
        }
    }

    // ── 마우스 ──────────────────────────────────────────────────────

    private FileItemViewModel? ItemAt(object source)
        => VisualTree.FindAncestor<ListViewItem>(source, List)?.DataContext as FileItemViewModel;

    private bool IsOnChrome(object source)
        => VisualTree.FindAncestor<ScrollBar>(source, List) is not null
           || VisualTree.FindAncestor<GridViewColumnHeader>(source, List) is not null;

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOnChrome(e.OriginalSource)) return;
        var item = ItemAt(e.OriginalSource);
        var onHandle = IsDragHandle(e.OriginalSource, item);
        _pressedItem = onHandle ? item : null;
        _dragStart = e.GetPosition(List);
        _deferSelect = false;
        _marqueePending = false;

        if (item is null)
        {
            // 빈 곳: 수정 키 없으면 선택 해제, 끌면 영역 선택
            if (Keyboard.Modifiers == ModifierKeys.None) List.SelectedItems.Clear();
            List.Focus();
            BeginMarqueePending(e);
            e.Handled = true;
            return;
        }

        if (item.IsEditing) return;

        if (e.ClickCount == 2)
        {
            e.Handled = true;
            Main?.OpenItem(_pane!, item);
            return;
        }

        if (!onHandle)
        {
            // 자세히 보기에서 이름 밖(날짜·크기 열 등): 클릭은 행 선택, 끌면 영역 선택
            BeginMarqueePending(e);
            return;
        }

        // 이미 선택된 항목을 누르면 드래그를 위해 다중 선택을 유지하고, 드래그 없이 떼면 그 항목만 선택
        if (List.SelectedItems.Contains(item) && Keyboard.Modifiers == ModifierKeys.None && List.SelectedItems.Count > 1)
        {
            _deferSelect = true;
            e.Handled = true;
            (List.ItemContainerGenerator.ContainerFromItem(item) as ListViewItem)?.Focus();
        }
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_marqueeActive)
        {
            EndMarquee();
            e.Handled = true;
        }

        _marqueePending = false;
        if (_deferSelect && _pressedItem is not null)
        {
            List.SelectItems(new[] { _pressedItem });
        }

        _deferSelect = false;
        _pressedItem = null;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (_marqueeActive)
        {
            UpdateMarquee();
            e.Handled = true; // ListView 자체의 끌기 선택(범위 선택)이 끼어들지 않도록
            return;
        }

        if (_marqueePending && e.LeftButton == MouseButtonState.Pressed)
        {
            var p = e.GetPosition(List);
            if (Math.Abs(p.X - _dragStart.X) >= SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(p.Y - _dragStart.Y) >= SystemParameters.MinimumVerticalDragDistance)
                StartMarquee();
            e.Handled = true;
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed || _pressedItem is not { IsPlaceholder: false, IsDrive: false, IsEditing: false } item || _tab is null)
            return;

        var pos = e.GetPosition(List);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var paths = _tab.SelectedPaths.ToList();
        if (!paths.Contains(item.FullPath, StringComparer.OrdinalIgnoreCase)) paths = [item.FullPath];
        _deferSelect = false;
        _pressedItem = null;

        var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
        if (Main is { } main) main.DragSourcePane = _pane;
        try
        {
            DragDrop.DoDragDrop(List, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            if (Main is { } m) m.DragSourcePane = null;
        }
    }

    // ── 마우스 끌기 영역 선택 ───────────────────────────────────────

    private Panel? _itemsHost;

    private Panel? ItemsHost => _itemsHost ??= VisualTree.FindDescendant<VirtualizingPanel>(List);

    private double ScrollY => _scroll?.VerticalOffset ?? 0;

    /// <summary>이름(아이콘·글자) 위인지. 아이콘 보기는 타일 전체가 끌기 핸들.</summary>
    private bool IsDragHandle(object source, FileItemViewModel? item)
    {
        if (item is null) return false;
        if (_appliedView != ViewMode.Details) return true;
        for (var d = source as DependencyObject; d is not null && d is not ListViewItem;
             d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement { Tag: "DragHandle" }) return true;
        }

        return false;
    }

    private Point PanelOrigin => ItemsHost?.TranslatePoint(default, List) ?? default;

    /// <summary>목록 좌표 → 패널 내용 좌표 (스크롤 오프셋 포함).</summary>
    private Point ToContent(Point listPoint)
    {
        var origin = PanelOrigin;
        return new Point(listPoint.X - origin.X, listPoint.Y - origin.Y + ScrollY);
    }

    private Point ToList(Point contentPoint)
    {
        var origin = PanelOrigin;
        return new Point(contentPoint.X + origin.X, contentPoint.Y + origin.Y - ScrollY);
    }

    private void BeginMarqueePending(MouseButtonEventArgs e)
    {
        _marqueePending = true;
        _marqueeStart = ToContent(e.GetPosition(List));
    }

    private void StartMarquee()
    {
        _marqueePending = false;
        _marqueeActive = true;
        _deferSelect = false;
        _pressedItem = null;

        // Ctrl/Shift를 누른 채 끌면 기존 선택에 추가, 아니면 새로 선택
        _marqueeBase = Keyboard.Modifiers is ModifierKeys.Control or ModifierKeys.Shift
            ? new HashSet<FileItemViewModel>(List.SelectedItems.Cast<FileItemViewModel>())
            : [];
        _marqueeLast = [];
        Marquee.Visibility = Visibility.Visible;
        List.CaptureMouse();
        _autoScroll.Start();
        UpdateMarquee();
    }

    private void UpdateMarquee()
    {
        var current = Mouse.GetPosition(List);
        var start = ToList(_marqueeStart);
        var rect = new Rect(start, current);
        rect.Intersect(new Rect(0, 0, List.ActualWidth, List.ActualHeight));
        if (!rect.IsEmpty)
        {
            Canvas.SetLeft(Marquee, rect.X);
            Canvas.SetTop(Marquee, rect.Y);
            Marquee.Width = rect.Width;
            Marquee.Height = rect.Height;
        }

        var hits = IndicesIn(new Rect(_marqueeStart, ToContent(current)));
        if (hits.SetEquals(_marqueeLast)) return;
        _marqueeLast = hits;
        List.SelectItems(_marqueeBase.Concat(hits).Distinct().ToList());
    }

    /// <summary>패널 내용 좌표 사각형과 겹치는 항목. 가상화로 화면 밖 행은 컨테이너가 없으므로 기하 계산으로 구한다.</summary>
    private HashSet<FileItemViewModel> IndicesIn(Rect rect)
    {
        var items = _tab?.Items;
        var result = new HashSet<FileItemViewModel>();
        if (items is null || items.Count == 0) return result;

        if (ItemsHost is VirtualizingWrapPanel wrap)
        {
            foreach (var i in wrap.IndicesIntersecting(rect, items.Count)) result.Add(items[i]);
            return result;
        }

        var rowHeight = (double)FindResource("RowHeight");
        if (rect.Bottom < 0 || rect.Top >= items.Count * rowHeight) return result;
        var first = Math.Max(0, (int)Math.Floor(rect.Top / rowHeight));
        var last = Math.Min(items.Count - 1, (int)Math.Floor(Math.Max(0, rect.Bottom - 0.01) / rowHeight));
        for (int i = first; i <= last; i++) result.Add(items[i]);
        return result;
    }

    private void AutoScrollMarquee()
    {
        if (!_marqueeActive || _scroll is null) return;
        var y = Mouse.GetPosition(List).Y;
        var top = PanelOrigin.Y;
        const double step = 24;
        if (y < top) _scroll.ScrollToVerticalOffset(ScrollY - step);
        else if (y > List.ActualHeight - 4) _scroll.ScrollToVerticalOffset(ScrollY + step);
        else return;
        Dispatcher.BeginInvoke(UpdateMarquee, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void EndMarquee()
    {
        _marqueeActive = false;
        _autoScroll.Stop();
        Marquee.Visibility = Visibility.Collapsed;
        if (List.IsMouseCaptured) List.ReleaseMouseCapture();

        // 마지막 결과를 확정 (캡처 해제 과정에서 ListView가 선택을 바꿨을 수 있으므로 다시 적용)
        var final = _marqueeBase.Concat(_marqueeLast).Distinct().ToList();
        Dispatcher.BeginInvoke(() =>
        {
            List.SelectItems(final);
            if (final.Count > 0 && List.ItemContainerGenerator.ContainerFromItem(final[^1]) is ListViewItem container) container.Focus();
            else List.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnListLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_marqueeActive) EndMarquee();
    }

    private void OnListMiddleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || ItemAt(e.OriginalSource) is not { } item) return;
        Main?.OpenInNewTab(_pane!, item);
        e.Handled = true;
    }

    private void OnListRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOnChrome(e.OriginalSource)) return;
        var item = ItemAt(e.OriginalSource);
        if (item is null) List.SelectedItems.Clear();
        else if (!List.SelectedItems.Contains(item)) List.SelectItems(new[] { item });
    }

    private void OnListRightMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Main is null || _pane is null || IsOnChrome(e.OriginalSource)) return;
        var item = ItemAt(e.OriginalSource);
        var screen = List.PointToScreen(e.GetPosition(List));
        Main.ShowContextMenu(_pane, (int)screen.X, (int)screen.Y,
            onBackground: item is null || item.IsPlaceholder,
            extended: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    // ── 끌어서 놓기 ─────────────────────────────────────────────────

    private DragDropEffects ComputeDropEffect(DragEventArgs e, out string? targetDirectory)
    {
        targetDirectory = null;
        if (_tab is null || _tab.IsThisPc || !e.Data.GetDataPresent(DataFormats.FileDrop)) return DragDropEffects.None;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return DragDropEffects.None;

        if (ItemAt(e.OriginalSource) is { IsDirectory: true, IsPlaceholder: false } folder) targetDirectory = folder.FullPath;
        var destination = targetDirectory ?? _tab.Path;
        if (files.Any(f => PathNames.IsSameOrChildOf(destination, f))) return DragDropEffects.None;

        DragDropEffects wanted;
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) wanted = DragDropEffects.Copy;
        else if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0) wanted = DragDropEffects.Move;
        else if (Main is { IsRightPaneVisible: true, DragSourcePane: { } source } && !ReferenceEquals(source, _pane))
        {
            // 두 창 사이 끌기: 기본은 복사, Shift를 누르면 이동
            wanted = DragDropEffects.Copy;
        }
        else
        {
            // 탐색기와 같은 기본 동작: 같은 드라이브면 이동, 다른 드라이브면 복사
            var sameRoot = string.Equals(Path.GetPathRoot(files[0]), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase);
            wanted = sameRoot ? DragDropEffects.Move : DragDropEffects.Copy;
        }

        if ((e.AllowedEffects & wanted) != 0) return wanted;
        return (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ComputeDropEffect(e, out _);
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var effect = ComputeDropEffect(e, out var targetDirectory);
        e.Handled = true;
        if (effect == DragDropEffects.None || Main is null || _tab is null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        Activate();
        await Main.DropFilesAsync(_tab, files, targetDirectory, move: effect == DragDropEffects.Move);
    }

    // ── 이름 바꾸기 (인라인) ────────────────────────────────────────

    private void OnRenameBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not TextBox box || box.DataContext is not FileItemViewModel item) return;
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            var dot = item.IsDirectory ? -1 : box.Text.LastIndexOf('.');
            box.Select(0, dot > 0 ? dot : box.Text.Length);
        }, DispatcherPriority.Input);
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not FileItemViewModel item) return;
        if (e.Key == Key.Enter)
        {
            CommitRename(item, box.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            item.IsEditing = false;
            FocusList();
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: FileItemViewModel { IsEditing: true } item } box) CommitRename(item, box.Text);
    }

    private void CommitRename(FileItemViewModel item, string text)
    {
        if (!item.IsEditing || Main is null || _tab is null) return;
        _ = Main.CommitRenameAsync(_tab, item, text);
        FocusList();
    }
}

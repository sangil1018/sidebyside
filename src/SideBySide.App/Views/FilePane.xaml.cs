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

    public FilePane()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachPane(DataContext as PaneViewModel);
        Loaded += (_, _) => EnsureHooks();
        IsVisibleChanged += (_, _) => EnsureHooks();
    }

    public MainViewModel? Main { get; set; }

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
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.SortBy) or nameof(TabViewModel.SortDescending))
            UpdateSortGlyphs();
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
        _pressedItem = item;
        _dragStart = e.GetPosition(List);
        _deferSelect = false;

        if (item is null)
        {
            // 빈 곳 클릭: 선택 해제
            List.SelectedItems.Clear();
            List.Focus();
            return;
        }

        if (item.IsEditing) return;

        if (e.ClickCount == 2)
        {
            e.Handled = true;
            Main?.OpenItem(_pane!, item);
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
        if (_deferSelect && _pressedItem is not null)
        {
            List.SelectItems(new[] { _pressedItem });
        }

        _deferSelect = false;
        _pressedItem = null;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
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
        DragDrop.DoDragDrop(List, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
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

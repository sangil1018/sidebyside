using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using SideBySide.App.Commands;
using SideBySide.App.ViewModels;
using SideBySide.App.Views;
using SideBySide.Core.Settings;
using SideBySide.Shell.Services;

namespace SideBySide.App;

/// <summary>
/// 메인 창. 코드 비하인드는 순수 UI 동작만 담당한다:
/// 사용자 지정 제목 표시줄, 창 위치 저장/복원, 열 너비(탐색 창/오른쪽 창/미리보기) 표시 전환, 비교 모드 스크롤 동기화.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private bool _syncingScroll;

    public MainWindow(MainViewModel viewModel, AppSettings settings, CommandHost commands)
    {
        _settings = settings;
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        commands.BindKeys(this, CommandScope.Global);
        LeftPane.Main = viewModel;
        RightPane.Main = viewModel;
        DiffView.Commands = commands;

        RestorePlacement();
        UpdateColumns();

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.FocusPaneRequested += (_, pane) => PaneView(pane).FocusList();
        viewModel.FocusAddressRequested += (_, pane) => PaneView(pane).FocusAddress();
        viewModel.FocusSearchRequested += (_, pane) => PaneView(pane).FocusSearch();
        viewModel.ContextMenuRequested += (_, pane) => PaneView(pane).ShowContextMenuForSelection();

        LeftPane.VerticalScrolled += (_, offset) => SyncScroll(RightPane, offset);
        RightPane.VerticalScrolled += (_, offset) => SyncScroll(LeftPane, offset);

        SourceInitialized += (_, _) => WindowTheming.SetRoundCorners(new WindowInteropHelper(this).Handle);
        StateChanged += (_, _) => UpdateMaximizedState();
        Closing += OnClosing;
        Loaded += (_, _) => LeftPane.FocusList();
    }

    public MainViewModel ViewModel { get; }

    private FilePane PaneView(PaneViewModel pane) => ReferenceEquals(pane, ViewModel.Left) ? LeftPane : RightPane;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsSidebarVisible):
            case nameof(MainViewModel.IsRightPaneVisible):
            case nameof(MainViewModel.ShowPreview):
                UpdateColumns();
                break;
            case nameof(MainViewModel.Mode):
                if (ViewModel.IsCompareMode)
                {
                    // 비교 모드에서는 좌우 같은 너비로 시작
                    LeftColumn.Width = new GridLength(1, GridUnitType.Star);
                    RightColumn.Width = new GridLength(1, GridUnitType.Star);
                }

                break;
            case nameof(MainViewModel.IsDiffOpen):
                if (ViewModel.IsDiffOpen) Dispatcher.BeginInvoke(DiffView.FocusContent);
                break;
        }
    }

    private void UpdateColumns()
    {
        var vm = ViewModel;

        if (vm.IsSidebarVisible)
        {
            if (SidebarColumn.Width.Value == 0) SidebarColumn.Width = new GridLength(Math.Max(140, _settings.SidebarWidth));
        }
        else if (SidebarColumn.Width.Value > 0)
        {
            _settings.SidebarWidth = SidebarColumn.ActualWidth;
            SidebarColumn.Width = new GridLength(0);
        }

        SidebarSplitter.Visibility = vm.IsSidebarVisible ? Visibility.Visible : Visibility.Collapsed;

        RightColumn.Width = vm.IsRightPaneVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        RightColumn.MinWidth = vm.IsRightPaneVisible ? 240 : 0;
        RightPane.Visibility = PaneSplitter.Visibility = vm.IsRightPaneVisible ? Visibility.Visible : Visibility.Collapsed;

        if (vm.ShowPreview)
        {
            if (PreviewColumn.Width.Value == 0) PreviewColumn.Width = new GridLength(Math.Max(200, _settings.PreviewWidth));
        }
        else if (PreviewColumn.Width.Value > 0)
        {
            _settings.PreviewWidth = PreviewColumn.ActualWidth;
            PreviewColumn.Width = new GridLength(0);
        }

        PreviewPane.Visibility = PreviewSplitter.Visibility = vm.ShowPreview ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncScroll(FilePane target, double offset)
    {
        if (_syncingScroll || !ViewModel.IsCompareMode) return;
        _syncingScroll = true;
        try { target.ScrollToVerticalOffset(offset); }
        finally { _syncingScroll = false; }
    }

    // ── 창 위치 ─────────────────────────────────────────────────────

    private void RestorePlacement()
    {
        var p = _settings.Window;
        Width = Math.Max(MinWidth, p.Width);
        Height = Math.Max(MinHeight, p.Height);

        var onScreen = !double.IsNaN(p.Left) && !double.IsNaN(p.Top)
                       && p.Left >= SystemParameters.VirtualScreenLeft - 50
                       && p.Top >= SystemParameters.VirtualScreenTop - 50
                       && p.Left + 100 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
                       && p.Top + 50 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (onScreen)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = p.Left;
            Top = p.Top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (p.Maximized) WindowState = WindowState.Maximized;
        SidebarColumn.Width = new GridLength(_settings.ShowSidebar ? Math.Max(140, _settings.SidebarWidth) : 0);
    }

    private void SavePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.Window = new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
        if (SidebarColumn.ActualWidth > 0) _settings.SidebarWidth = SidebarColumn.ActualWidth;
        if (PreviewColumn.ActualWidth > 0) _settings.PreviewWidth = PreviewColumn.ActualWidth;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!ViewModel.CanClose())
        {
            e.Cancel = true;
            return;
        }

        ViewModel.SaveState();
        SavePlacement();
    }

    // ── 제목 표시줄 ─────────────────────────────────────────────────

    private void UpdateMaximizedState()
    {
        var maximized = WindowState == WindowState.Maximized;

        // WindowChrome 창은 최대화 시 화면 밖으로 테두리만큼 넘치므로 안쪽 여백으로 보정
        RootBorder.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaxButton.Content = maximized ? "" : "";
        MaxButton.ToolTip = maximized ? "이전 크기로 복원" : "최대화";
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnNewMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = NewButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(MenuItemFor(CommandIds.NewFolder, ""));
        menu.Items.Add(MenuItemFor(CommandIds.NewTextFile, ""));
        menu.IsOpen = true;
    }

    private MenuItem MenuItemFor(string id, string glyph)
    {
        var def = CommandRegistry.Get(id);
        return new MenuItem
        {
            Header = def.Title,
            InputGestureText = def.Gesture,
            Command = ViewModel.Commands[id],
            Icon = new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph") },
        };
    }
}

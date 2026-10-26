using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using SideBySide.App.Commands;
using SideBySide.App.Infrastructure;
using SideBySide.App.Modules;
using SideBySide.App.Services;
using SideBySide.App.ViewModels;
using SideBySide.App.Views;
using SideBySide.Core.Settings;
using SideBySide.Shell.Services;
using SideBySide.Core.Localization;

namespace SideBySide.App;

/// <summary>
/// 메인 창. 코드 비하인드는 순수 UI 동작만 담당한다:
/// 사용자 지정 제목 표시줄, 창 위치 저장/복원, 열 너비(탐색 창/오른쪽 창/미리보기) 표시 전환, 비교 모드 스크롤 동기화.
/// </summary>
public partial class MainWindow : Window
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int WM_HOTKEY = 0x0312;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    private readonly AppSettings _settings;
    private readonly System.Windows.Threading.DispatcherTimer _deviceTimer;
    private bool _syncingScroll;

    /// <summary>오른쪽 열에 지금 보이는 것 (너비를 각각 따로 기억).</summary>
    private enum SideColumn
    {
        None,
        Preview,
        Tools,
    }

    private SideColumn _side;

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
        viewModel.Tools.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModuleManager.HasPanelTools)) UpdateColumns();
        };
        viewModel.FocusPaneRequested += (_, pane) => PaneView(pane).FocusList();
        viewModel.FocusAddressRequested += (_, pane) => PaneView(pane).FocusAddress();
        viewModel.FocusSearchRequested += (_, pane) => PaneView(pane).FocusSearch();
        viewModel.ContextMenuRequested += (_, pane) => PaneView(pane).ShowContextMenuForSelection();
        viewModel.OpenShortcutChanged += (_, _) => RegisterOpenShortcut();
        PreviewMouseDown += OnPreviewMouseDown;

        LeftPane.VerticalScrolled += (_, offset) => SyncScroll(RightPane, offset);
        RightPane.VerticalScrolled += (_, offset) => SyncScroll(LeftPane, offset);

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowTheming.SetRoundCorners(hwnd);
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            RegisterOpenShortcut();
            Closed += (_, _) => GlobalHotkey.Unregister(hwnd);
        };
        _deviceTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _deviceTimer.Tick += (_, _) =>
        {
            _deviceTimer.Stop();
            _ = ViewModel.OnDevicesChangedAsync();
        };
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
            case nameof(MainViewModel.IsDualPane):
                if (ViewModel.IsDualPane)
                {
                    // 두 창을 켜면 좌우 같은 너비로 시작
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

        // 오른쪽 열: 열린 도구가 있으면 도구 패널, 아니면 미리보기 (너비는 각자 저장)
        var side = vm.Tools.HasPanelTools ? SideColumn.Tools : vm.ShowPreview ? SideColumn.Preview : SideColumn.None;
        if (side != _side)
        {
            RememberSideWidth();
            PreviewColumn.Width = side switch
            {
                SideColumn.Tools => new GridLength(Math.Max(300, _settings.ToolPanelWidth)),
                SideColumn.Preview => new GridLength(Math.Max(200, _settings.PreviewWidth)),
                _ => new GridLength(0),
            };
            PreviewColumn.MinWidth = side == SideColumn.Tools ? 300 : 0;
            _side = side;
        }

        PreviewPane.Visibility = side == SideColumn.Preview ? Visibility.Visible : Visibility.Collapsed;
        ToolPanel.Visibility = side == SideColumn.Tools ? Visibility.Visible : Visibility.Collapsed;
        PreviewSplitter.Visibility = side == SideColumn.None ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RememberSideWidth()
    {
        if (PreviewColumn.ActualWidth <= 0) return;
        if (_side == SideColumn.Tools) _settings.ToolPanelWidth = PreviewColumn.ActualWidth;
        else if (_side == SideColumn.Preview) _settings.PreviewWidth = PreviewColumn.ActualWidth;
    }

    private void SyncScroll(FilePane target, double offset)
    {
        if (_syncingScroll || !ViewModel.IsCompareMode) return;
        _syncingScroll = true;
        try { target.ScrollToVerticalOffset(offset); }
        finally { _syncingScroll = false; }
    }

    /// <summary>USB 장치 연결/분리 감지 → 드라이브 목록 갱신 (연속 알림은 묶어서 한 번만).</summary>
    /// <summary>마우스 추가 버튼: 뒤로(XButton1) / 앞으로(XButton2). 마우스가 있는 창의 탭에 적용한다.</summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2)) return;
        e.Handled = true;
        if (ViewModel.IsDiffOpen) return; // 파일 비교 화면에는 탐색 기록이 없다

        var pane = VisualTree.FindAncestor<FilePane>(e.OriginalSource)?.DataContext as PaneViewModel ?? ViewModel.ActivePane;
        ViewModel.GoHistory(pane, back: e.ChangedButton == MouseButton.XButton1);
    }

    /// <summary>앱 전역 단축키로 창을 앞으로 가져온다 (앱이 실행 중일 때만 동작).</summary>
    private void RegisterOpenShortcut()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        GlobalHotkey.Unregister(hwnd);

        // 앱이 꺼져 있어도 쓰는 설정이면 백그라운드 에이전트가 키를 갖는다 (같은 키는 한 곳만 등록 가능)
        if (HotkeyAgent.IsEnabled)
        {
            try
            {
                HotkeyAgent.EnsureRunning(_settings.OpenShortcut);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ViewModel.ShowNotice(L.T("단축키 백그라운드 실행을 시작하지 못했습니다: ") + ex.Message);
            }

            return;
        }

        var (modifiers, key) = HotkeyAgent.KeysFor(_settings.OpenShortcut);
        if (!GlobalHotkey.Register(hwnd, modifiers, key))
            ViewModel.ShowNotice(L.T("창 열기 단축키를 등록하지 못했습니다. 다른 프로그램이 같은 키를 쓰고 있을 수 있습니다."));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == GlobalHotkey.Id)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_DEVICECHANGE && (wParam.ToInt32() == DBT_DEVICEARRIVAL || wParam.ToInt32() == DBT_DEVICEREMOVECOMPLETE))
        {
            _deviceTimer.Stop();
            _deviceTimer.Start();
        }

        return IntPtr.Zero;
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
        RememberSideWidth();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!ViewModel.CanClose())
        {
            e.Cancel = true;
            return;
        }

        SavePlacement();
        ViewModel.Tools.Dispose(); // 모듈 창 위치·데이터를 설정에 남기고 소켓·작업자를 정리 (설정 저장 전)
        ViewModel.SaveState();
    }

    // ── 제목 표시줄 ─────────────────────────────────────────────────

    private void UpdateMaximizedState()
    {
        var maximized = WindowState == WindowState.Maximized;

        // WindowChrome 창은 최대화 시 화면 밖으로 테두리만큼 넘치므로 안쪽 여백으로 보정
        RootBorder.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaxButton.Content = maximized ? "" : "";
        MaxButton.ToolTip = maximized ? L.T("이전 크기로 복원") : L.T("최대화");
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

    /// <summary>도구 모듈 메뉴: 켜진 모듈만. 현재 선택을 대상으로 연다.</summary>
    private void OnToolsMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ToolsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var module in ViewModel.Tools.EnabledModules)
        {
            var def = CommandRegistry.Get(module.CommandId);
            menu.Items.Add(new MenuItem
            {
                Header = module.Title,
                InputGestureText = def.Gesture,
                Command = ViewModel.Commands[module.CommandId],
                Icon = new TextBlock { Text = module.Glyph, Style = (Style)FindResource("Glyph") },
                ToolTip = module.Description,
            });
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = L.T("모듈 설정…"),
            Command = ViewModel.Commands[CommandIds.OpenSettings],
            Icon = new TextBlock { Text = SideBySide.Modules.Glyphs.Settings, Style = (Style)FindResource("Glyph") },
        });
        menu.IsOpen = true;
    }

    private void OnViewMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ViewButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (id, mode) in new[]
                 {
                     (CommandIds.ViewLargeIcons, ViewMode.LargeIcons),
                     (CommandIds.ViewMediumIcons, ViewMode.MediumIcons),
                     (CommandIds.ViewDetails, ViewMode.Details),
                 })
        {
            menu.Items.Add(MenuItemFor(id, ViewModel.ViewMode == mode ? "\uE73E" : string.Empty));
        }

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

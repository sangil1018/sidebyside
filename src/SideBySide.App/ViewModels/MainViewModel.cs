using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SideBySide.App.Commands;
using SideBySide.App.Infrastructure;
using SideBySide.App.Services;
using SideBySide.Core.Diff;
using SideBySide.Core.Filtering;
using SideBySide.Core.FileSystem;
using SideBySide.Core.Rename;
using SideBySide.Core.Sessions;
using SideBySide.Core.Settings;

namespace SideBySide.App.ViewModels;

public enum AppMode
{
    Explorer,
    Compare,
}

/// <summary>
/// 메인 창 조정자: 모드/레이아웃 상태를 갖고 모든 명령(<see cref="CommandIds"/>)의 동작을 등록한다.
/// 실제 일은 탭/비교/파일 비교 뷰모델과 셸 서비스에 위임한다.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ExplorerContext _ctx;
    private readonly ThemeManager _theme;
    private readonly UndoService _undo;
    private readonly DispatcherTimer _noticeTimer;
    private CancellationTokenSource? _freeSpaceCts;
    private string? _freeSpaceRoot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompareMode), nameof(IsRightPaneVisible), nameof(IsSidebarVisible))]
    private AppMode _mode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRightPaneVisible))]
    private bool _isDualPane;

    [ObservableProperty]
    private bool _showPreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidebarVisible))]
    private bool _showSidebar;

    [ObservableProperty]
    private bool _showHidden;

    [ObservableProperty]
    private PaneViewModel _activePane;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDiffOpen))]
    private FileDiffViewModel? _activeDiff;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _selectionText = string.Empty;

    [ObservableProperty]
    private string _hiddenText = string.Empty;

    [ObservableProperty]
    private string _freeSpaceText = string.Empty;

    [ObservableProperty]
    private string _windowTitle = "SideBySide";

    /// <summary>상태 표시줄에 잠깐 보여줄 알림 (실행 취소 결과 등).</summary>
    [ObservableProperty]
    private string _notice = string.Empty;

    [ObservableProperty]
    private ViewMode _viewMode;

    public MainViewModel(ExplorerContext ctx, ThemeManager theme, CommandHost commands, UndoService undo)
    {
        _ctx = ctx;
        _theme = theme;
        _undo = undo;
        Commands = commands;
        _noticeTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) =>
        {
            _noticeTimer!.Stop();
            Notice = string.Empty;
        }, Dispatcher.CurrentDispatcher);
        _noticeTimer.Stop();
        _undo.Changed += (_, _) => Commands.RaiseCanExecuteChanged();
        var s = ctx.Settings;

        Left = new PaneViewModel(ctx, PaneSide.Left, s.Left);
        Right = new PaneViewModel(ctx, PaneSide.Right, s.Right);
        Sidebar = new SidebarViewModel(ctx);
        Compare = new CompareViewModel(ctx);
        Preview = new PreviewViewModel();

        _isDualPane = s.DualPane;
        _showPreview = s.ShowPreview;
        _showSidebar = s.ShowSidebar;
        _showHidden = s.ShowHidden;
        _viewMode = s.ViewMode;
        _activePane = Left;
        Left.IsActive = true;

        foreach (var pane in new[] { Left, Right })
        {
            pane.StateChanged += (_, _) => OnPaneStateChanged(pane);
            pane.PropertyChanged += OnPanePropertyChanged;
        }

        Compare.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CompareViewModel.Summary)) UpdateStatus();
        };
        Compare.FileDiffRequested += (_, files) => _ = OpenFileDiffAsync(files.Left, files.Right);
        ctx.FilterChanged += (_, _) => OnPropertyChanged(nameof(FilterSummary));

        RegisterCommands();
    }

    public CommandHost Commands { get; }

    public PaneViewModel Left { get; }

    public PaneViewModel Right { get; }

    public SidebarViewModel Sidebar { get; }

    public CompareViewModel Compare { get; }

    public PreviewViewModel Preview { get; }

    public bool IsCompareMode => Mode == AppMode.Compare;

    public bool IsRightPaneVisible => IsDualPane || IsCompareMode;

    public bool IsSidebarVisible => ShowSidebar && !IsCompareMode;

    public bool IsDiffOpen => ActiveDiff is not null;

    public TabViewModel ActiveTab => ActivePane.ActiveTab;

    public PaneViewModel OtherPane => ReferenceEquals(ActivePane, Left) ? Right : Left;

    /// <summary>상단 필터 칩에 표시할 요약. 필터가 없으면 빈 문자열.</summary>
    public string FilterSummary
    {
        get
        {
            var f = _ctx.Settings.Filter;
            if (f.IsEmpty) return string.Empty;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(f.Exclude)) parts.Add("제외 " + string.Join(", ", FilterSet.SplitPatterns(f.Exclude)));
            if (!string.IsNullOrWhiteSpace(f.Include)) parts.Add("포함 " + string.Join(", ", FilterSet.SplitPatterns(f.Include)));
            return (f.Name.Length > 0 ? f.Name + ": " : string.Empty) + string.Join(" / ", parts);
        }
    }

    // ── 뷰와의 연결 (뷰가 구독) ──────────────────────────────────────

    public event EventHandler<PaneViewModel>? FocusPaneRequested;

    public event EventHandler<PaneViewModel>? FocusAddressRequested;

    public event EventHandler<PaneViewModel>? FocusSearchRequested;

    /// <summary>뷰가 선택 항목 위치를 계산해 <see cref="ShowContextMenu"/>를 호출하도록 요청.</summary>
    public event EventHandler<PaneViewModel>? ContextMenuRequested;

    // ── 시작 / 종료 ─────────────────────────────────────────────────

    public async Task InitializeAsync(IReadOnlyList<string> args)
    {
        _ = Sidebar.RefreshDrivesAsync();

        if (args.Count >= 2)
        {
            var (a, b) = (TabViewModel.NormalizePath(args[0]), TabViewModel.NormalizePath(args[1]));
            if (File.Exists(a) && File.Exists(b))
            {
                IsDualPane = true;
                await Task.WhenAll(Left.ActiveTab.NavigateAsync(a), Right.ActiveTab.NavigateAsync(b));
                await OpenFileDiffAsync(a, b);
                return;
            }

            if (Directory.Exists(a) && Directory.Exists(b))
            {
                Left.ActiveTab.ApplyNavigation(NavKind.Open, a);
                Right.ActiveTab.ApplyNavigation(NavKind.Open, b);
                Mode = AppMode.Compare;
                return;
            }
        }

        if (args.Count == 1)
        {
            await Task.WhenAll(Left.ActiveTab.NavigateAsync(args[0]), Right.LoadActiveAsync());
            return;
        }

        await Task.WhenAll(Left.LoadActiveAsync(), Right.LoadActiveAsync());
    }

    public void SaveState()
    {
        var s = _ctx.Settings;
        s.Left = Left.SaveState();
        s.Right = Right.SaveState();
        s.DualPane = IsDualPane;
        s.ShowPreview = ShowPreview;
        s.ShowSidebar = ShowSidebar;
        s.ViewMode = ViewMode;
        Compare.SaveOptions();
    }

    /// <summary>창 닫기 전 확인 (파일 비교에 저장 안 된 변경이 있으면).</summary>
    public bool CanClose() => ActiveDiff?.ConfirmClose() ?? true;

    // ── 뷰에서 직접 호출 ────────────────────────────────────────────

    public void Activate(PaneViewModel pane)
    {
        if (!ReferenceEquals(ActivePane, pane)) ActivePane = pane;
    }

    public void OpenItem(PaneViewModel pane, FileItemViewModel item)
    {
        Activate(pane);
        if (item.IsPlaceholder) return;
        if (IsCompareMode && Compare.TryOpen(item)) return;

        if (item.IsDirectory)
        {
            _ = pane.ActiveTab.NavigateAsync(item.FullPath);
            return;
        }

        Run(() => _ctx.Launcher.Open(item.FullPath));
    }

    public void OpenInNewTab(PaneViewModel pane, FileItemViewModel item)
    {
        if (item.IsDirectory && !item.IsPlaceholder) pane.NewTab(item.FullPath, activate: false);
    }

    public async Task CommitRenameAsync(TabViewModel tab, FileItemViewModel item, string newName)
    {
        item.IsEditing = false;
        newName = newName.Trim();
        if (newName.Length == 0 || newName == item.Name) return;
        if (PathNames.Validate(newName) is { } error)
        {
            _ctx.Dialogs.ShowError(error);
            return;
        }

        var oldPath = item.FullPath;
        var oldName = item.Name;
        if (await _ctx.FileOperations.RenameAsync(oldPath, newName))
        {
            var newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, newName);
            _undo.Push("이름 바꾸기", () => _ctx.FileOperations.RenameAsync(newPath, oldName));
            await RefreshAfterChangeAsync(tab, [newName]);
        }
    }

    /// <summary>Ctrl+마우스 휠: 자세히 ↔ 중간 아이콘 ↔ 큰 아이콘.</summary>
    public void CycleViewMode(int direction)
    {
        if (IsCompareMode) return;
        var next = Math.Clamp((int)ViewMode + Math.Sign(direction), 0, (int)ViewMode.LargeIcons);
        ViewMode = (ViewMode)next;
    }

    public void ShowNotice(string text)
    {
        Notice = text;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    public void ShowContextMenu(PaneViewModel pane, int screenX, int screenY, bool onBackground, bool extended)
    {
        Activate(pane);
        var tab = pane.ActiveTab;
        var paths = tab.SelectedPaths;
        Run(() =>
        {
            if (!onBackground && paths.Count > 0)
                _ctx.ContextMenu.ShowForItems(paths, screenX, screenY, extended, HandleShellVerb);
            else if (!tab.IsThisPc && Directory.Exists(tab.Path))
                _ctx.ContextMenu.ShowForFolder(tab.Path, screenX, screenY, extended, HandleShellVerb);
        });
    }

    public void NavigateActive(string path) => _ = ActiveTab.NavigateAsync(path);

    public async Task DropFilesAsync(TabViewModel target, IReadOnlyList<string> files, string? targetDirectory, bool move)
    {
        var destination = targetDirectory ?? target.Path;
        if (destination.Length == 0 || files.Count == 0) return;
        if (files.Any(f => PathNames.IsSameOrChildOf(destination, f))) return; // 자기 자신/하위로 이동 금지
        if (files.All(f => string.Equals(Path.GetDirectoryName(f), destination, StringComparison.OrdinalIgnoreCase))) return;

        var ok = await TransferAsync(files, destination, move);
        if (ok) await RefreshAfterChangeAsync(target, files.Select(Path.GetFileName).OfType<string>().ToList());
    }

    // ── 명령 등록 ───────────────────────────────────────────────────

    private void RegisterCommands()
    {
        var c = Commands;

        // 모드·레이아웃
        c.Register(CommandIds.ToggleMode, ToggleMode);
        c.Register(CommandIds.ToggleDualPane, () => { if (!IsCompareMode) IsDualPane = !IsDualPane; });
        c.Register(CommandIds.TogglePreview, () => ShowPreview = !ShowPreview);
        c.Register(CommandIds.ToggleSidebar, () => ShowSidebar = !ShowSidebar);
        c.Register(CommandIds.ToggleHidden, () => ShowHidden = !ShowHidden);
        c.Register(CommandIds.ToggleTheme, () => _ctx.Settings.Theme = _theme.Cycle());
        c.Register(CommandIds.ViewDetails, () => ViewMode = ViewMode.Details);
        c.Register(CommandIds.ViewMediumIcons, () => { if (!IsCompareMode) ViewMode = ViewMode.MediumIcons; });
        c.Register(CommandIds.ViewLargeIcons, () => { if (!IsCompareMode) ViewMode = ViewMode.LargeIcons; });
        // canExecute가 false면 키 입력이 처리되지 않고 컨트롤 기본 동작(Tab 이동 등)으로 넘어간다
        c.Register(CommandIds.SwitchPane, () =>
        {
            ActivePane = OtherPane;
            FocusPaneRequested?.Invoke(this, ActivePane);
        }, () => IsRightPaneVisible);

        // 탭
        c.Register(CommandIds.NewTab, () => ActivePane.NewTab());
        c.Register(CommandIds.CloseTab, () => ActivePane.CloseTab(null));
        c.Register(CommandIds.NextTab, () => ActivePane.CycleTab(1));
        c.Register(CommandIds.PrevTab, () => ActivePane.CycleTab(-1));

        // 탐색
        c.Register(CommandIds.Back, () => _ = ActiveTab.GoBackAsync());
        c.Register(CommandIds.Forward, () => _ = ActiveTab.GoForwardAsync());
        c.Register(CommandIds.Up, () => _ = ActiveTab.GoUpAsync());
        c.Register(CommandIds.Refresh, Refresh);
        c.Register(CommandIds.FocusAddress, () => FocusAddressRequested?.Invoke(this, ActivePane));
        c.Register(CommandIds.FocusSearch, () => FocusSearchRequested?.Invoke(this, ActivePane));
        c.Register(CommandIds.SearchSubfolders, () => ActiveTab.SearchSubfolders = !ActiveTab.SearchSubfolders);
        c.Register(CommandIds.Cancel, Cancel, () => (IsCompareMode && Compare.IsRunning) || ActiveTab.SearchText.Length > 0 || _ctx.CutPaths.Count > 0);

        // 파일 작업
        c.Register(CommandIds.Open, OpenSelection);
        c.Register(CommandIds.Undo, () => _ = UndoAsync());
        c.Register(CommandIds.Rename, Rename);
        c.Register(CommandIds.Delete, () => _ = DeleteAsync(permanent: false));
        c.Register(CommandIds.DeletePermanent, () => _ = DeleteAsync(permanent: true));
        c.Register(CommandIds.Copy, () => SetClipboard(cut: false));
        c.Register(CommandIds.Cut, () => SetClipboard(cut: true));
        c.Register(CommandIds.Paste, () => _ = PasteAsync());
        c.Register(CommandIds.NewFolder, () => CreateNew(folder: true));
        c.Register(CommandIds.NewTextFile, () => CreateNew(folder: false));
        c.Register(CommandIds.Properties, () =>
        {
            var target = ActiveTab.SelectedPaths.FirstOrDefault() ?? ActiveTab.Path;
            if (target.Length > 0) Run(() => _ctx.Launcher.ShowProperties(target));
        });
        c.Register(CommandIds.CopyPath, () =>
        {
            var paths = ActiveTab.SelectedPaths;
            var text = paths.Count > 0 ? string.Join("\r\n", paths.Select(p => $"\"{p}\"")) : ActiveTab.Path;
            if (text.Length > 0) _ctx.Clipboard.SetText(text);
        });
        c.Register(CommandIds.ShowContextMenu, () => ContextMenuRequested?.Invoke(this, ActivePane));
        c.Register(CommandIds.OpenTerminal, () =>
        {
            if (!ActiveTab.IsThisPc) Run(() => _ctx.Launcher.OpenTerminal(ActiveTab.Path));
        });
        c.Register(CommandIds.PinFavorite, () =>
        {
            var target = ActiveTab.SelectedItem is { IsDirectory: true, IsPlaceholder: false } dir ? dir.FullPath : ActiveTab.Path;
            Sidebar.Pin(target);
        });
        c.Register(CommandIds.CopyToRight, () => _ = CopyBetweenPanesAsync(Left, Right), () => IsRightPaneVisible);
        c.Register(CommandIds.CopyToLeft, () => _ = CopyBetweenPanesAsync(Right, Left), () => IsRightPaneVisible);

        // 필터
        c.Register(CommandIds.EditFilter, EditFilter);
        c.Register(CommandIds.ClearFilter, () => _ctx.SetFilter(FilterSettings.None));

        // 비교
        c.Register(CommandIds.NextDifference, () => GoToDifference(forward: true), () => IsCompareMode);
        c.Register(CommandIds.PrevDifference, () => GoToDifference(forward: false), () => IsCompareMode);
        c.Register(CommandIds.ToggleDifferencesOnly, () => Compare.ShowDifferencesOnly = !Compare.ShowDifferencesOnly);
        c.Register(CommandIds.CompareSelectedFiles, CompareSelectedFiles);
        c.Register(CommandIds.SaveSession, SaveSession);
        c.Register(CommandIds.OpenSession, () => _ = OpenSessionAsync());

        // 파일 비교 화면
        c.Register(CommandIds.DiffNext, () => ActiveDiff?.Next());
        c.Register(CommandIds.DiffPrev, () => ActiveDiff?.Previous());
        c.Register(CommandIds.DiffApplyToRight, () => ActiveDiff?.Apply(DiffSide.Right));
        c.Register(CommandIds.DiffApplyToLeft, () => ActiveDiff?.Apply(DiffSide.Left));
        c.Register(CommandIds.DiffSave, () => ActiveDiff?.Save());
        c.Register(CommandIds.DiffUndo, () => ActiveDiff?.Undo());
        c.Register(CommandIds.DiffToggleEdit, () => ActiveDiff?.ToggleEdit());
        c.Register(CommandIds.DiffClose, CloseDiff);
    }

    // ── 명령 구현 ───────────────────────────────────────────────────

    private void ToggleMode()
    {
        if (ActiveDiff is not null)
        {
            if (!ActiveDiff.ConfirmClose()) return;
            ActiveDiff = null;
        }

        Mode = IsCompareMode ? AppMode.Explorer : AppMode.Compare;
    }

    partial void OnModeChanged(AppMode value)
    {
        if (value == AppMode.Compare) Compare.Enter(Left.ActiveTab, Right.ActiveTab);
        else Compare.Exit();
        UpdateStatus();
        FocusPaneRequested?.Invoke(this, ActivePane);
    }

    partial void OnIsDualPaneChanged(bool value)
    {
        if (!value && ReferenceEquals(ActivePane, Right) && !IsCompareMode) ActivePane = Left;
    }

    partial void OnShowHiddenChanged(bool value) => _ctx.SetShowHidden(value);

    partial void OnShowPreviewChanged(bool value)
    {
        if (value) _ = Preview.ShowAsync(ActiveTab.SelectedItem);
        else Preview.Clear();
    }

    partial void OnActivePaneChanged(PaneViewModel value)
    {
        Left.IsActive = ReferenceEquals(value, Left);
        Right.IsActive = ReferenceEquals(value, Right);
        OnPaneStateChanged(value);
    }

    partial void OnActiveDiffChanged(FileDiffViewModel? value)
    {
        if (value is null) FocusPaneRequested?.Invoke(this, ActivePane);
    }

    private void Refresh()
    {
        if (IsCompareMode) _ = Compare.RunAsync(clearCache: true, keepSelection: true);
        else _ = ActiveTab.RefreshAsync();
        _ = Sidebar.RefreshDrivesAsync();
    }

    private void Cancel()
    {
        if (IsCompareMode && Compare.IsRunning) Compare.Cancel();
        else if (ActiveTab.SearchText.Length > 0) ActiveTab.SearchText = string.Empty;
        else if (_ctx.CutPaths.Count > 0) _ctx.SetCut([]);
    }

    private void OpenSelection()
    {
        var items = ActiveTab.SelectedItems.Where(i => !i.IsPlaceholder).ToList();
        if (items.Count == 0) return;
        if (items.Count == 1)
        {
            OpenItem(ActivePane, items[0]);
            return;
        }

        foreach (var file in items.Where(i => !i.IsDirectory).Take(20))
            Run(() => _ctx.Launcher.Open(file.FullPath));
    }

    private void Rename()
    {
        var tab = ActiveTab;
        var items = tab.SelectedItems.Where(i => !i.IsPlaceholder && !i.IsDrive).ToList();
        if (items.Count == 1)
        {
            BeginRename(tab, items[0]);
        }
        else if (items.Count > 1 && _ctx.Dialogs.BatchRename(items.Select(i => i.FullPath).ToList()) is { Count: > 0 } applied)
        {
            var reverse = applied.Select(p => new RenamePreview(p.NewPath, p.OriginalName, null)).ToList();
            _undo.Push($"이름 바꾸기 ({applied.Count}개)", () => Task.Run(() =>
            {
                try
                {
                    BatchRenamer.Apply(reverse);
                    return true;
                }
                catch (BatchRenameException)
                {
                    return false;
                }
            }));
            _ = RefreshAfterChangeAsync(tab, []);
        }
    }

    private static void BeginRename(TabViewModel tab, FileItemViewModel item)
    {
        item.EditName = item.Name;
        tab.RequestSelect([item.Name], beginRename: true);
    }

    private async Task DeleteAsync(bool permanent)
    {
        var tab = ActiveTab;
        var paths = tab.SelectedPaths;
        if (paths.Count == 0) return;
        if (!await _ctx.FileOperations.DeleteAsync(paths, permanent)) return;
        if (!permanent)
        {
            _undo.Push($"삭제 ({paths.Count}개)", async () =>
            {
                var restored = await _ctx.FileOperations.RestoreFromRecycleBinAsync(paths);
                return restored.Count == paths.Count;
            });
        }

        await RefreshAfterChangeAsync(tab, []);
    }

    private void SetClipboard(bool cut)
    {
        var paths = ActiveTab.SelectedPaths;
        if (paths.Count == 0) return;
        _ctx.Clipboard.SetFiles(paths, cut);
        _ctx.SetCut(cut ? paths : []);
    }

    private async Task PasteAsync()
    {
        var tab = ActiveTab;
        if (tab.IsThisPc || _ctx.Clipboard.GetFiles() is not { } clip || clip.Paths.Count == 0) return;

        var sameFolder = clip.Paths.All(p => string.Equals(Path.GetDirectoryName(p), tab.Path, StringComparison.OrdinalIgnoreCase));
        if (clip.Cut && sameFolder) return;
        var ok = await TransferAsync(clip.Paths, tab.Path, move: clip.Cut, renameOnCollision: !clip.Cut && sameFolder);
        if (ok && clip.Cut)
        {
            _ctx.Clipboard.Clear();
            _ctx.SetCut([]);
        }

        if (ok) await RefreshAfterChangeAsync(tab, clip.Paths.Select(Path.GetFileName).OfType<string>().ToList());
    }

    private async void CreateNew(bool folder)
    {
        var tab = ActiveTab;
        if (tab.IsThisPc) return;
        try
        {
            var path = folder ? _ctx.FileOperations.CreateFolder(tab.Path) : _ctx.FileOperations.CreateTextFile(tab.Path);
            var name = Path.GetFileName(path);
            _undo.Push(folder ? "새 폴더 만들기" : "새 파일 만들기", () => _ctx.FileOperations.DeleteAsync([path], permanent: false));
            if (tab.IsCompareView) await Compare.RunAsync(clearCache: true, select: [name]);
            else await tab.LoadAsync([name]);

            var item = tab.Items.FirstOrDefault(i => i.Name == name && !i.IsPlaceholder);
            if (item is not null) BeginRename(tab, item);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ctx.Dialogs.ShowError(ex.Message);
        }
    }

    private async Task CopyBetweenPanesAsync(PaneViewModel from, PaneViewModel to)
    {
        if (!IsRightPaneVisible) return;
        var paths = from.ActiveTab.SelectedPaths;
        var destination = to.ActiveTab.Path;
        if (paths.Count == 0 || destination.Length == 0) return;

        if (IsCompareMode && !Directory.Exists(destination)) Directory.CreateDirectory(destination);
        if (await TransferAsync(paths, destination, move: false)) await RefreshAfterChangeAsync(to.ActiveTab, []);
    }

    private void EditFilter()
    {
        var result = _ctx.Dialogs.EditFilter(_ctx.Settings.Filter, _ctx.Settings.FilterPresets);
        if (result is not null) _ctx.SetFilter(result);
    }

    private void GoToDifference(bool forward)
    {
        if (!IsCompareMode) return;
        var tab = ActiveTab;
        var from = tab.SelectedItem is { } item ? tab.Items.IndexOf(item) : -1;
        var index = Compare.FindDifference(tab, from, forward);
        if (index >= 0) tab.RequestSelectIndices([index]);
    }

    private void CompareSelectedFiles()
    {
        var active = ActiveTab.SelectedItems.Where(i => !i.IsPlaceholder && !i.IsDirectory).ToList();
        if (IsCompareMode && ActiveTab.SelectedItem is { Row: { Left: not null, Right: not null } row } && !row.IsDirectory)
        {
            _ = OpenFileDiffAsync(row.Left.FullPath, row.Right.FullPath);
            return;
        }

        if (active.Count == 2)
        {
            _ = OpenFileDiffAsync(active[0].FullPath, active[1].FullPath);
            return;
        }

        var left = Left.ActiveTab.SelectedItem;
        var right = Right.ActiveTab.SelectedItem;
        if (IsRightPaneVisible && left is { IsDirectory: false, IsPlaceholder: false } && right is { IsDirectory: false, IsPlaceholder: false })
            _ = OpenFileDiffAsync(left.FullPath, right.FullPath);
        else
            _ctx.Dialogs.ShowError("비교할 파일을 왼쪽·오른쪽 창에서 하나씩 선택하거나, 한 창에서 두 개를 선택하세요.");
    }

    private void SaveSession()
    {
        var left = Left.ActiveTab.Path;
        var right = Right.ActiveTab.Path;
        if (left.Length == 0 || right.Length == 0)
        {
            _ctx.Dialogs.ShowError("양쪽 창에서 비교할 폴더를 연 뒤 저장하세요.");
            return;
        }

        var name = _ctx.Dialogs.Prompt("비교 세션 저장", "세션 이름", $"{Path.GetFileName(left)} ↔ {Path.GetFileName(right)}");
        if (string.IsNullOrWhiteSpace(name)) return;

        var sessions = _ctx.Settings.Sessions;
        sessions.RemoveAll(s => s.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        sessions.Insert(0, new CompareSession
        {
            Name = name.Trim(),
            LeftPath = left,
            RightPath = right,
            Filter = _ctx.Settings.Filter,
            Compare = Compare.Options,
            Diff = _ctx.Settings.Diff,
            ShowDifferencesOnly = Compare.ShowDifferencesOnly,
        });
    }

    private async Task OpenSessionAsync()
    {
        var session = _ctx.Dialogs.PickSession(_ctx.Settings.Sessions);
        if (session is null) return;
        if (ActiveDiff is not null)
        {
            if (!ActiveDiff.ConfirmClose()) return;
            ActiveDiff = null;
        }

        if (IsCompareMode) Mode = AppMode.Explorer;
        _ctx.Settings.Diff = session.Diff;
        Compare.Method = session.Compare.Method;
        Compare.TimeToleranceSeconds = session.Compare.TimeToleranceSeconds;
        Compare.IgnoreHourOffset = session.Compare.IgnoreHourOffset;
        Compare.CompareSubfolders = session.Compare.CompareSubfolders;
        Compare.ShowDifferencesOnly = session.ShowDifferencesOnly;
        _ctx.SetFilter(session.Filter);

        Left.ActiveTab.ApplyNavigation(NavKind.Open, TabViewModel.NormalizePath(session.LeftPath));
        Right.ActiveTab.ApplyNavigation(NavKind.Open, TabViewModel.NormalizePath(session.RightPath));
        Mode = AppMode.Compare;
        await Task.CompletedTask;
    }

    private async Task OpenFileDiffAsync(string left, string right)
    {
        if (ActiveDiff is not null)
        {
            if (!ActiveDiff.ConfirmClose()) return;
            ActiveDiff = null;
        }

        ActiveDiff = await FileDiffViewModel.LoadAsync(left, right, _ctx.Settings.Diff, _ctx.Dialogs, o => _ctx.Settings.Diff = o);
    }

    private void CloseDiff()
    {
        if (ActiveDiff is null || !ActiveDiff.ConfirmClose()) return;
        ActiveDiff = null;
        if (IsCompareMode) _ = Compare.RunAsync(clearCache: true, keepSelection: true);
    }

    // ── 상태 ────────────────────────────────────────────────────────

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PaneViewModel.ActiveTab) || sender is not PaneViewModel pane) return;
        if (IsCompareMode)
        {
            // 비교 중 탭을 바꾸면 새 탭 기준으로 다시 비교
            Compare.Exit();
            Compare.Enter(Left.ActiveTab, Right.ActiveTab);
        }

        if (ReferenceEquals(pane, ActivePane)) OnPropertyChanged(nameof(ActiveTab));
    }

    private void OnPaneStateChanged(PaneViewModel pane)
    {
        if (!ReferenceEquals(pane, ActivePane)) return;
        UpdateStatus();
        if (ShowPreview) _ = Preview.ShowAsync(ActiveTab.SelectedItem);
    }

    private void UpdateStatus()
    {
        var tab = ActiveTab;
        WindowTitle = $"{tab.Title} - SideBySide";
        StatusText = IsCompareMode ? Compare.Summary
            : tab.IsSearching ? $"검색 중… {tab.Items.Count:N0}개 찾음"
            : tab.IsRecursiveSearch ? $"검색 결과 {tab.Items.Count:N0}개"
            : $"항목 {tab.Items.Count:N0}개";

        var selected = tab.SelectedItems.Where(i => !i.IsPlaceholder).ToList();
        if (selected.Count == 0)
        {
            SelectionText = string.Empty;
        }
        else
        {
            var files = selected.Where(i => !i.IsDirectory).ToList();
            SelectionText = files.Count > 0
                ? $"{selected.Count:N0}개 선택 · {Formatting.Size(files.Sum(f => f.Size))}"
                : $"{selected.Count:N0}개 선택";
        }

        HiddenText = tab.HiddenCount > 0 ? $"필터로 {tab.HiddenCount:N0}개 숨김" : string.Empty;
        _ = UpdateFreeSpaceAsync(tab.Path);
    }

    private async Task UpdateFreeSpaceAsync(string path)
    {
        var root = path.Length > 0 ? Path.GetPathRoot(path) : null;
        if (string.Equals(root, _freeSpaceRoot, StringComparison.OrdinalIgnoreCase)) return;
        _freeSpaceRoot = root;
        _freeSpaceCts?.Cancel();
        var cts = _freeSpaceCts = new CancellationTokenSource();
        if (string.IsNullOrEmpty(root))
        {
            FreeSpaceText = string.Empty;
            return;
        }

        try
        {
            var free = await Task.Run(() => new DriveInfo(root).AvailableFreeSpace, cts.Token);
            if (!cts.IsCancellationRequested) FreeSpaceText = $"여유 공간 {Formatting.Size(free)}";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (!cts.IsCancellationRequested) FreeSpaceText = string.Empty;
        }
    }

    // ── 도우미 ──────────────────────────────────────────────────────

    private async Task RefreshAfterChangeAsync(TabViewModel tab, IReadOnlyCollection<string> select)
    {
        if (IsCompareMode) await Compare.RunAsync(clearCache: true, select: select.Count > 0 ? select : null, keepSelection: select.Count == 0);
        else await tab.LoadAsync(select.Count > 0 ? select : tab.SelectedItems.Select(i => i.Name).ToList());
    }

    /// <summary>
    /// 복사/이동 + 실행 취소 기록. 작업 전후로 대상 폴더 목록을 비교해 새로 생긴 항목만 되돌린다
    /// (덮어쓴 항목은 원래 내용을 복구할 수 없으므로 실행 취소 대상에서 제외).
    /// </summary>
    private async Task<bool> TransferAsync(IReadOnlyList<string> sources, string destination, bool move, bool renameOnCollision = false)
    {
        var before = SnapshotNames(destination);
        var ok = move
            ? await _ctx.FileOperations.MoveAsync(sources, destination)
            : await _ctx.FileOperations.CopyAsync(sources, destination, renameOnCollision);

        var created = SnapshotNames(destination).Except(before, StringComparer.OrdinalIgnoreCase)
            .Select(name => Path.Combine(destination, name))
            .ToList();
        if (created.Count == 0) return ok;

        if (move)
        {
            var origins = sources
                .Select(src => (Moved: Path.Combine(destination, Path.GetFileName(src)), OriginalDir: Path.GetDirectoryName(src)!))
                .Where(m => created.Contains(m.Moved, StringComparer.OrdinalIgnoreCase))
                .ToList();
            _undo.Push($"이동 ({origins.Count}개)", async () =>
            {
                var all = true;
                foreach (var group in origins.GroupBy(o => o.OriginalDir, StringComparer.OrdinalIgnoreCase))
                    all &= await _ctx.FileOperations.MoveAsync(group.Select(g => g.Moved).ToList(), group.Key);
                return all;
            });
        }
        else
        {
            _undo.Push($"복사 ({created.Count}개)", () => _ctx.FileOperations.DeleteAsync(created, permanent: false));
        }

        return ok;
    }

    private static HashSet<string> SnapshotNames(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? new HashSet<string>(Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task UndoAsync()
    {
        var result = await _undo.UndoAsync();
        if (result is null)
        {
            ShowNotice("실행 취소할 작업이 없습니다.");
            return;
        }

        ShowNotice(result.Value.Succeeded
            ? $"실행 취소: {result.Value.Description}"
            : $"실행 취소하지 못했습니다: {result.Value.Description} (항목이 바뀌었거나 휴지통에 없습니다)");

        if (IsCompareMode)
        {
            await Compare.RunAsync(clearCache: true, keepSelection: true);
        }
        else
        {
            await ActiveTab.RefreshAsync();
            if (IsRightPaneVisible) await OtherPane.ActiveTab.RefreshAsync();
        }
    }

    private bool HandleShellVerb(string verb)
    {
        if (!verb.Equals("rename", StringComparison.OrdinalIgnoreCase)) return false;
        Rename();
        return true;
    }

    /// <summary>셸 호출 실패를 오류 대화상자로.</summary>
    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            _ctx.Dialogs.ShowError(ex.Message);
        }
    }
}

using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SideBySide.App.Infrastructure;
using SideBySide.App.Services;
using SideBySide.App.ViewModels;
using SideBySide.App.Views;
using SideBySide.Core.FileSystem;
using SideBySide.Core.Localization;
using SideBySide.Core.Settings;
using SideBySide.Modules;
using SideBySide.Shell.Abstractions;

namespace SideBySide.App.Modules;

/// <summary>오른쪽 도구 패널에 열린 도구 하나 (패널 위쪽 탭).</summary>
public sealed class PanelTool(string id, string title, string glyph, FrameworkElement view)
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    public string Glyph { get; } = glyph;

    public FrameworkElement View { get; } = view;
}

/// <summary>
/// 모듈 수명 관리: 켜기/끄기, 처음 사용할 때 생성, 화면을 오른쪽 패널 또는 별도 창에 표시, 화면을 닫거나 끄면 해제.
/// 꺼진 모듈은 인스턴스가 없고(설정 값만 남음), 열지 않은 모듈은 어셈블리도 로드되지 않는다.
/// </summary>
public sealed partial class ModuleManager(AppSettings settings, ThemeManager theme) : ObservableObject, IDisposable
{
    private sealed class Running(ModuleDescriptor descriptor, IToolModule module)
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;

        public IToolModule Module { get; } = module;

        public ModuleWindow? Window { get; set; }

        public PanelTool? Panel { get; set; }

        public bool IsShown => Window is not null || Panel is not null;
    }

    private readonly Dictionary<string, Running> _running = [];
    private MainViewModel? _main;

    // ── Git 패널 자동 열기 상태 (SyncGitPanel) ──
    private string? _gitFolder;        // 마지막으로 본 활성 폴더
    private bool _gitSinglePane;       // 그때 한 창 모드였는지
    private string? _gitRoot;          // 그 폴더가 속한 저장소 (없으면 null)
    private string? _gitDismissedRoot; // 사용자가 패널을 닫은 저장소: 이 저장소를 벗어날 때까지 다시 열지 않는다
    private bool _gitAutoOpened;       // 지금 떠 있는 Git 패널을 앱이 스스로 열었는지 (그럴 때만 스스로 닫는다)
    private bool _gitSynced;           // SyncGitPanel이 한 번이라도 불렸는지
    private int _gitSyncVersion;

    /// <summary>오른쪽 패널에 열린 도구들.</summary>
    public ObservableCollection<PanelTool> PanelTools { get; } = [];

    [ObservableProperty]
    private PanelTool? _activePanelTool;

    public bool HasPanelTools => PanelTools.Count > 0;

    public IReadOnlyList<ModuleDescriptor> EnabledModules => ModuleCatalog.All.Where(IsEnabled).ToList();

    public bool HasEnabledModules => ModuleCatalog.All.Any(IsEnabled);

    public void Attach(MainViewModel main) => _main = main;

    public bool IsEnabled(ModuleDescriptor module) => settings.Modules.TryGetValue(module.Id, out var s) && s.Enabled;

    public ModuleOpenMode OpenModeOf(ModuleDescriptor module)
    {
        var mode = settings.Modules.TryGetValue(module.Id, out var s) ? s.OpenMode : ModuleOpenMode.Default;
        return mode == ModuleOpenMode.Default ? module.DefaultOpenMode : mode;
    }

    /// <summary>실행 중인 모듈 수 (진단·테스트용).</summary>
    public int RunningCount => _running.Count;

    /// <summary>모듈을 연다 (켜져 있을 때만). 이미 열려 있으면 앞으로 가져오고 새 대상을 넘긴다.</summary>
    public void Open(string id, ModuleInvocation invocation)
    {
        if (ModuleCatalog.Find(id) is not { } descriptor || !IsEnabled(descriptor) || _main is null) return;

        if (id == ModuleCatalog.GitId)
        {
            // 사용자가 직접 열면 그때부터는 사용자 것: 자동으로 닫지 않고, 닫았던 기억도 지운다
            var shown = _running.TryGetValue(id, out var existing) && existing.IsShown;
            _gitAutoOpened = invocation.Entry == ModuleEntry.Auto && (!shown || _gitAutoOpened);
            if (invocation.Entry != ModuleEntry.Auto) _gitDismissedRoot = null;
        }

        Running running;
        try
        {
            running = GetOrCreate(descriptor);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorLog.Write(ex);
            MessageBox.Show(Application.Current.MainWindow!, L.F("'{0}' 도구를 시작하지 못했습니다.\n{1}", descriptor.Title, ex.Message),
                "SideBySide", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (OpenModeOf(descriptor) == ModuleOpenMode.Window) ShowInWindow(running);
        else ShowInPanel(running);

        running.Module.Open(invocation);
    }

    /// <summary>도구 메뉴·단축키: 현재 선택을 대상으로 연다.</summary>
    public void OpenFromToolbar(string id)
    {
        if (_main is null) return;
        var tab = _main.ActiveTab;
        var folder = tab.IsThisPc || TabViewModel.IsVirtualFolder(tab.Path) ? null : tab.Path;
        Open(id, new ModuleInvocation(tab.SelectedPaths, folder, ModuleEntry.Toolbar));
    }

    public void BringToFront(string id)
    {
        if (!_running.TryGetValue(id, out var running)) return;

        // 화면 없이 백그라운드로 돌던 모듈(예: 받기 대기)에 일이 생기면 화면을 다시 연다
        if (!running.IsShown)
        {
            if (OpenModeOf(running.Descriptor) == ModuleOpenMode.Window) ShowInWindow(running);
            else ShowInPanel(running);
        }

        if (running.Window is { } window)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }
        else if (running.Panel is { } panel)
        {
            ActivePanelTool = panel;
            Application.Current.MainWindow?.Activate();
        }
    }

    public Window? WindowOf(string id) => _running.TryGetValue(id, out var r) && r.Window is { } w ? w : null;

    /// <summary>패널 탭의 닫기 단추.</summary>
    public void ClosePanelTool(PanelTool tool)
    {
        if (!_running.TryGetValue(tool.Id, out var running) || !running.Module.CanClose()) return;
        if (tool.Id == ModuleCatalog.GitId)
        {
            // 닫은 저장소 안을 돌아다니는 동안에는 다시 자동으로 열지 않는다
            _gitDismissedRoot = _gitRoot;
            _gitAutoOpened = false;
        }

        RemoveFromPanel(running);
        OnUiClosed(running);
    }

    /// <summary>
    /// 활성 폴더나 창 구성이 바뀌었다: 한 창 모드에서 git 저장소 폴더에 들어가면 Git 패널을 열고, 벗어나면 (앱이 연 경우에만) 닫는다.
    /// Git 모듈이 켜져 있고 모듈의 '자동으로 열기'가 켜져 있으며 열기 방식이 패널일 때만 동작한다.
    /// </summary>
    public void SyncGitPanel(string? folder, bool singlePane)
    {
        if (_gitSynced && string.Equals(folder, _gitFolder, StringComparison.OrdinalIgnoreCase) && singlePane == _gitSinglePane) return;
        _gitSynced = true;
        _gitFolder = folder;
        _gitSinglePane = singlePane;
        _ = SyncGitPanelAsync(folder, singlePane);
    }

    private async Task SyncGitPanelAsync(string? folder, bool singlePane)
    {
        var version = ++_gitSyncVersion;
        if (ModuleCatalog.Find(ModuleCatalog.GitId) is not { } descriptor || !IsEnabled(descriptor) || _main is null) return;

        // 저장소 찾기는 상위 폴더를 훑으므로 (느린 드라이브 대비) UI 스레드 밖에서
        var root = folder is null ? null : await Task.Run(() => GitRepository.FindRoot(folder));
        if (version != _gitSyncVersion) return;
        _gitRoot = root;
        if (root is null || !GitRepository.SameRoot(root, _gitDismissedRoot)) _gitDismissedRoot = null;

        var shown = _running.TryGetValue(descriptor.Id, out var running) && running.IsShown;
        if (shown && root is not null && (singlePane || !_gitAutoOpened))
        {
            // 이미 열려 있으면(직접 열었든 자동이든) 보고 있는 저장소만 따라간다. 패널의 활성 탭은 건드리지 않는다.
            running!.Module.Open(new ModuleInvocation([], root, ModuleEntry.Auto));
            return;
        }

        if (shown)
        {
            // 저장소를 벗어났거나 두 창으로 바뀌었다: 앱이 연 패널만 닫는다
            if (_gitAutoOpened && running!.Panel is not null && running.Module.CanClose())
            {
                _gitAutoOpened = false;
                RemoveFromPanel(running);
                OnUiClosed(running);
            }

            return;
        }

        var wanted = singlePane && root is not null && _gitDismissedRoot is null
                     && GitAutoOpenEnabled() && OpenModeOf(descriptor) == ModuleOpenMode.Panel;
        if (wanted) Open(descriptor.Id, new ModuleInvocation([], root, ModuleEntry.Auto));
    }

    /// <summary>Git 모듈의 '저장소 폴더에서 자동으로 열기' (모듈 데이터에 저장, 없으면 켜짐). 모듈 어셈블리를 불러오지 않고 읽는다.</summary>
    private bool GitAutoOpenEnabled()
    {
        if (!settings.Modules.TryGetValue(ModuleCatalog.GitId, out var s) || s.Data is null) return true;
        foreach (var (name, value) in s.Data)
        {
            if (!name.Equals("AutoOpen", StringComparison.OrdinalIgnoreCase) || value is null) continue;
            try
            {
                return value.GetValue<bool>();
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                return true;
            }
        }

        return true;
    }

    /// <summary>패널에 있는 도구를 별도 창으로 옮긴다 (이 모듈의 열기 방식도 '별도 창'으로 기억).</summary>
    public void PopOut(PanelTool tool)
    {
        if (!_running.TryGetValue(tool.Id, out var running)) return;
        SettingsOf(running.Descriptor.Id).OpenMode = ModuleOpenMode.Window;
        ShowInWindow(running);
    }

    /// <summary>설정 창에서 켜기/끄기·열기 방식이 바뀌었다. 끈 모듈은 화면을 닫고 즉시 해제한다.</summary>
    public void Apply(IReadOnlyDictionary<string, (bool Enabled, ModuleOpenMode Mode)> changes)
    {
        foreach (var (id, (enabled, mode)) in changes)
        {
            var s = SettingsOf(id);
            var modeChanged = s.OpenMode != mode;
            s.Enabled = enabled;
            s.OpenMode = mode;

            if (!_running.TryGetValue(id, out var running)) continue;
            if (!enabled)
            {
                Shutdown(running);
            }
            else if (modeChanged && running.IsShown)
            {
                // 열린 화면을 새 방식으로 옮긴다
                if (OpenModeOf(running.Descriptor) == ModuleOpenMode.Window) ShowInWindow(running);
                else ShowInPanel(running);
            }
        }

        OnPropertyChanged(nameof(HasEnabledModules));
        OnPropertyChanged(nameof(EnabledModules));

        // Git 모듈을 방금 켰거나 열기 방식을 바꿨으면 지금 폴더 기준으로 다시 판단한다
        if (_gitSynced && changes.ContainsKey(ModuleCatalog.GitId)) _ = SyncGitPanelAsync(_gitFolder, _gitSinglePane);
    }

    /// <summary>우클릭 메뉴 맨 위에 넣을 모듈 항목. 켜진 모듈 중 대상에 맞는 것만.</summary>
    public IReadOnlyList<ShellMenuItem> BuildMenu(MenuContext context)
    {
        var items = new List<ShellMenuItem>();
        foreach (var module in ModuleCatalog.All)
        {
            if (module.MenuText is null || module.AppliesTo is null || !IsEnabled(module)) continue;

            bool applies;
            try
            {
                applies = module.AppliesTo(context);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                applies = false;
            }

            if (!applies) continue;

            IReadOnlyList<string> paths = context.OnBackground ? (context.Folder is null ? [] : [context.Folder]) : context.Paths;
            var id = module.Id;
            var invocation = new ModuleInvocation(paths, context.Folder, ModuleEntry.ContextMenu);

            // 실행 중인 모듈만 동적 항목을 준다 (메뉴를 그리려고 모듈을 만들지 않는다)
            var dynamic = _running.TryGetValue(id, out var running) ? SafeDynamicItems(running, paths) : [];
            if (dynamic.Count == 0)
            {
                items.Add(new ShellMenuItem(module.MenuText, module.Glyph, () => Open(id, invocation)));
                continue;
            }

            var children = dynamic
                .Select(d => new ShellMenuItem(d.Text, null, () => Open(id, invocation with { Action = d.Action })))
                .Append(new ShellMenuItem(module.MenuText, null, () => Open(id, invocation)))
                .ToList();
            items.Add(new ShellMenuItem(module.Title, module.Glyph, null, children));
        }

        return items;
    }

    /// <summary>앱을 닫아도 되는지 (작업 중인 모듈이 있으면 각 모듈이 묻는다).</summary>
    public bool CanCloseAll() => _running.Values.All(r => r.Module.CanClose());

    /// <summary>창 위치 저장 등 종료 전 정리.</summary>
    public void Dispose()
    {
        foreach (var running in _running.Values.ToList()) Shutdown(running);
    }

    private static IReadOnlyList<ModuleMenuItem> SafeDynamicItems(Running running, IReadOnlyList<string> paths)
    {
        try
        {
            return running.Module.GetDynamicMenuItems(paths);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return [];
        }
    }

    private Running GetOrCreate(ModuleDescriptor descriptor)
    {
        if (_running.TryGetValue(descriptor.Id, out var running)) return running;
        var host = new ModuleHost(descriptor, this, _main!, settings);
        running = new Running(descriptor, descriptor.Create(host));
        _running[descriptor.Id] = running;
        return running;
    }

    private ModuleSettings SettingsOf(string id)
    {
        if (!settings.Modules.TryGetValue(id, out var s)) settings.Modules[id] = s = new ModuleSettings();
        return s;
    }

    private void ShowInPanel(Running running)
    {
        if (running.Window is { } window)
        {
            running.Window = null;
            window.CloseWithoutNotify(); // 화면을 창에서 떼어 패널로 옮긴다
        }

        if (running.Panel is null)
        {
            running.Panel = new PanelTool(running.Descriptor.Id, running.Descriptor.Title, running.Descriptor.Glyph, running.Module.View);
            PanelTools.Add(running.Panel);
            OnPropertyChanged(nameof(HasPanelTools));
        }

        ActivePanelTool = running.Panel;
    }

    private void ShowInWindow(Running running)
    {
        if (running.Panel is not null) RemoveFromPanel(running);

        if (running.Window is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var s = SettingsOf(running.Descriptor.Id);
        var window = new ModuleWindow(running.Descriptor.Title, running.Module.View, s, theme)
        {
            Owner = Application.Current.MainWindow,
        };
        window.CloseRequested += (_, e) => e.Cancel = !running.Module.CanClose();
        window.ClosedByUser += (_, _) =>
        {
            running.Window = null;
            OnUiClosed(running);
        };
        running.Window = window;
        window.Show();
    }

    private void RemoveFromPanel(Running running)
    {
        if (running.Panel is not { } panel) return;
        var index = PanelTools.IndexOf(panel);
        running.Panel = null;
        PanelTools.Remove(panel);
        if (ReferenceEquals(ActivePanelTool, panel))
            ActivePanelTool = PanelTools.Count == 0 ? null : PanelTools[Math.Clamp(index - 1, 0, PanelTools.Count - 1)];
        OnPropertyChanged(nameof(HasPanelTools));
    }

    /// <summary>화면이 닫혔다: 무거운 자원을 놓고, 백그라운드 서비스가 없으면 모듈을 해제한다.</summary>
    private void OnUiClosed(Running running)
    {
        if (running.IsShown) return;
        try { running.Module.OnClosed(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { ErrorLog.Write(ex); }

        if (!running.Module.KeepsRunningWhenClosed) Release(running);
    }

    private void Shutdown(Running running)
    {
        RemoveFromPanel(running);
        if (running.Window is { } window)
        {
            running.Window = null;
            window.CloseWithoutNotify();
        }

        try { running.Module.OnClosed(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { ErrorLog.Write(ex); }

        Release(running);
    }

    private void Release(Running running)
    {
        _running.Remove(running.Descriptor.Id);
        try { running.Module.Dispose(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException) { ErrorLog.Write(ex); }
    }
}

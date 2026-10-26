using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SideBySide.App.Infrastructure;
using SideBySide.App.Services;
using SideBySide.App.ViewModels;
using SideBySide.App.Views;
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
        RemoveFromPanel(running);
        OnUiClosed(running);
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

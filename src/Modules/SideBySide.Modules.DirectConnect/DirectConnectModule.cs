using System.Windows;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.DirectConnect;

/// <summary>
/// Direct Connect 모듈 (wifi-direct 이식, AgentZi Direct Connect 앱과 와이어 호환).
/// 탐색·받기 서버는 화면이 열려 있거나 '화면을 닫아도 받기'가 켜진 동안에만 돈다. 모듈을 끄면 소켓을 모두 닫는다.
/// </summary>
public sealed class DirectConnectModule(IModuleHost host) : ToolModuleBase(host)
{
    private const string SendPrefix = "send:";
    private DirectConnectViewModel? _vm;

    public override bool KeepsRunningWhenClosed => _vm?.KeepRunning == true;

    public override void Open(ModuleInvocation invocation)
    {
        _ = View;
        if (_vm is null) return;
        _vm.Start();

        var files = invocation.Paths.Where(File.Exists).ToList();
        if (invocation.Action is { } action && action.StartsWith(SendPrefix, StringComparison.Ordinal))
        {
            _vm.SendTo(action[SendPrefix.Length..], files);
        }
        else if (files.Count > 0)
        {
            _vm.QueueFiles(files);
        }
    }

    /// <summary>실행 중일 때만: 지금 보이는 기기로 바로 보내기.</summary>
    public override IReadOnlyList<ModuleMenuItem> GetDynamicMenuItems(IReadOnlyList<string> paths)
    {
        if (_vm is null || paths.Count == 0 || !paths.All(File.Exists)) return [];
        return _vm.CurrentPeers
            .Select(p => new ModuleMenuItem(L.F("{0}(으)로 보내기", p.Name), SendPrefix + p.Id))
            .ToList();
    }

    public override void OnClosed()
    {
        _vm?.Save();
        if (_vm?.KeepRunning != true) _vm?.Stop();
    }

    protected override FrameworkElement CreateView()
    {
        _vm = new DirectConnectViewModel(Host, Application.Current.Dispatcher);
        return new DirectConnectView { DataContext = _vm };
    }

    protected override void Dispose(bool disposing) => _vm?.Dispose();
}

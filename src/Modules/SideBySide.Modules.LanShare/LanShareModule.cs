using System.Windows;

namespace SideBySide.Modules.LanShare;

/// <summary>LAN 직접 연결 공유 모듈 (lan_share_setup 이식). 폴더를 우클릭해 열면 그 폴더를 공유 폴더로 고른다.</summary>
public sealed class LanShareModule(IModuleHost host) : ToolModuleBase(host)
{
    private LanShareViewModel? _vm;

    public override void Open(ModuleInvocation invocation)
    {
        _ = View;
        if (invocation.Entry == ModuleEntry.ContextMenu && invocation.Paths is [var folder] && Directory.Exists(folder)) _vm?.UseFolder(folder);
    }

    public override void OnClosed() => _vm?.Save();

    protected override FrameworkElement CreateView()
    {
        _vm = new LanShareViewModel(Host);
        return new LanShareView { DataContext = _vm };
    }
}

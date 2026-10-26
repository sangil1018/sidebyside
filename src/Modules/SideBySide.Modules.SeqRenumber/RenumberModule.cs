using System.Windows;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.SeqRenumber;

/// <summary>시퀀스 번호 다시 매기기 모듈 (seq-renumber 이식).</summary>
public sealed class RenumberModule(IModuleHost host) : ToolModuleBase(host)
{
    private RenumberViewModel? _vm;

    public override void Open(ModuleInvocation invocation)
    {
        _ = View;
        var paths = invocation.Paths.Count > 0 ? invocation.Paths : invocation.Folder is { } f ? [f] : [];
        if (paths.Count > 0 && _vm is not null) _ = _vm.LoadAsync(paths);
    }

    public override bool CanClose() => ConfirmCancelRunning(_vm?.IsBusy == true, L.T("시퀀스 번호 다시 매기기"));

    public override void OnClosed()
    {
        _vm?.Cancel();
        _vm?.Save();
    }

    protected override FrameworkElement CreateView()
    {
        _vm = new RenumberViewModel(Host);
        return new RenumberView { DataContext = _vm };
    }

    protected override void Dispose(bool disposing) => _vm?.Cancel();
}

using System.Windows;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.Resize;

/// <summary>이미지 크기 조정 모듈 (file-resizer 이식).</summary>
public sealed class ResizeModule(IModuleHost host) : ToolModuleBase(host)
{
    private ResizeViewModel? _vm;

    public override void Open(ModuleInvocation invocation)
    {
        _ = View;
        if (invocation.Paths.Count > 0 && _vm is { IsBusy: false }) _vm.SetInputs(invocation.Paths);
    }

    public override bool CanClose() => ConfirmCancelRunning(_vm?.IsBusy == true, L.T("이미지 크기 조정"));

    public override void OnClosed()
    {
        _vm?.Cancel();
        _vm?.Save();
    }

    protected override FrameworkElement CreateView()
    {
        _vm = new ResizeViewModel(Host);
        return new ResizeView { DataContext = _vm };
    }

    protected override void Dispose(bool disposing) => _vm?.Cancel();
}

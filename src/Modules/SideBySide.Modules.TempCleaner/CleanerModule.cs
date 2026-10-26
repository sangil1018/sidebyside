using System.Windows;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.TempCleaner;

/// <summary>임시 파일 정리 모듈 (TempCleaner 이식). 처음 열 때 자동으로 검사한다 (원본과 같음).</summary>
public sealed class CleanerModule(IModuleHost host) : ToolModuleBase(host)
{
    private CleanerViewModel? _vm;
    private bool _scanned;

    public override void Open(ModuleInvocation invocation)
    {
        _ = View;
        if (_scanned || _vm is null) return;
        _scanned = true;
        _vm.ScanCommand.Execute(null);
    }

    public override bool CanClose() => ConfirmCancelRunning(_vm?.IsBusy == true, L.T("임시 파일 정리"));

    public override void OnClosed()
    {
        _vm?.Cancel();
        _vm?.Save();
    }

    protected override FrameworkElement CreateView()
    {
        _vm = new CleanerViewModel(Host);
        return new CleanerView(_vm);
    }

    protected override void Dispose(bool disposing) => _vm?.Cancel();
}

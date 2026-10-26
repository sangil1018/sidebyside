using System.Windows;

namespace SideBySide.Modules.SeqViewer;

/// <summary>시퀀스 뷰어 모듈 (seq-viewer 이식, 보기 전용). 화면을 닫으면 디코드 작업자와 프레임 캐시를 모두 해제한다.</summary>
public sealed class ViewerModule(IModuleHost host) : ToolModuleBase(host)
{
    private ViewerViewModel? _vm;
    private FrameCache? _cache;
    private ViewerView? _view;

    public override void Open(ModuleInvocation invocation)
    {
        _ = View;
        var paths = invocation.Paths.Count > 0 ? invocation.Paths : invocation.Folder is { } f ? [f] : [];
        if (paths.Count > 0 && _vm is not null) _ = _vm.LoadAsync(paths);
    }

    public override void OnClosed()
    {
        _vm?.Stop();
        _vm?.Save();
        _view?.Release();
        _cache?.Dispose();
        _cache = null;
    }

    protected override FrameworkElement CreateView()
    {
        _vm = new ViewerViewModel(Host);
        _cache = new FrameCache(Application.Current.Dispatcher, FrameCache.DefaultBudget());
        _view = new ViewerView(_vm, _cache);
        return _view;
    }

    protected override void Dispose(bool disposing)
    {
        _vm?.Stop();
        _view?.Release();
        _cache?.Dispose();
        _cache = null;
    }
}

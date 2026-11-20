using System.Windows;
using SideBySide.Core.FileSystem;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.Svn;

/// <summary>
/// SVN 패널 모듈: 작업 사본의 변경을 보고 커밋·업데이트·브랜치 전환을 한다 (Git 패널과 같은 흐름).
/// 한 창 모드에서 작업 사본 폴더에 들어가면 앱이 자동으로 연다 (<see cref="SvnSettings.AutoOpen"/>).
/// </summary>
public sealed class SvnModule(IModuleHost host) : ToolModuleBase(host)
{
    private SvnViewModel? _vm;

    public override void Open(ModuleInvocation invocation)
    {
        _ = View;
        // 폴더 하나를 우클릭했으면 그 폴더, 아니면 지금 보고 있는 폴더가 속한 작업 사본
        var target = invocation.Paths.Count == 1 && Directory.Exists(invocation.Paths[0]) ? invocation.Paths[0]
            : invocation.Folder ?? (invocation.Paths.Count > 0 ? Path.GetDirectoryName(invocation.Paths[0]) : Host.ActiveFolder);
        _vm?.SetWorkingCopy(SvnWorkingCopy.FindRoot(target));
    }

    public override bool CanClose()
    {
        if (_vm?.IsBusy != true) return true;
        // 자동으로 닫힐 때도 불리므로 묻지 않고 알리기만 한다
        Host.ShowNotice(L.T("SVN 작업이 끝난 뒤 닫을 수 있습니다."));
        return false;
    }

    public override void OnClosed() => _vm?.Suspend();

    protected override FrameworkElement CreateView()
    {
        _vm = new SvnViewModel(Host);
        return new SvnView { DataContext = _vm };
    }

    protected override void Dispose(bool disposing) => _vm?.Dispose();
}

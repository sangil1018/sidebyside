using System.Windows;
using SideBySide.Core.FileSystem;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.Git;

/// <summary>
/// Git 패널 모듈: 저장소의 변경을 보고 커밋·브랜치 전환·원격 동기화를 한다 (GitHub Desktop의 일상 작업).
/// 한 창 모드에서 저장소 폴더에 들어가면 앱이 자동으로 연다 (<see cref="GitSettings.AutoOpen"/>).
/// </summary>
public sealed class GitModule(IModuleHost host) : ToolModuleBase(host)
{
    private GitViewModel? _vm;

    public override void Open(ModuleInvocation invocation)
    {
        _ = View;
        // 폴더 하나를 우클릭했으면 그 폴더, 아니면 지금 보고 있는 폴더가 속한 저장소
        var target = invocation.Paths.Count == 1 && Directory.Exists(invocation.Paths[0]) ? invocation.Paths[0]
            : invocation.Folder ?? (invocation.Paths.Count > 0 ? Path.GetDirectoryName(invocation.Paths[0]) : Host.ActiveFolder);
        _vm?.SetRepository(GitRepository.FindRoot(target));
    }

    public override bool CanClose()
    {
        if (_vm?.IsBusy != true) return true;
        // 자동으로 닫힐 때도 불리므로 묻지 않고 알리기만 한다
        Host.ShowNotice(L.T("Git 작업이 끝난 뒤 닫을 수 있습니다."));
        return false;
    }

    public override void OnClosed() => _vm?.Suspend();

    protected override FrameworkElement CreateView()
    {
        _vm = new GitViewModel(Host);
        return new GitView { DataContext = _vm };
    }

    protected override void Dispose(bool disposing) => _vm?.Dispose();
}

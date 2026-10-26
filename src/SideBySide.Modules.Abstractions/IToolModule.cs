using System.Windows;

namespace SideBySide.Modules;

/// <summary>모듈을 연 경로.</summary>
public enum ModuleEntry
{
    /// <summary>파일 목록 우클릭 메뉴.</summary>
    ContextMenu,

    /// <summary>도구 모음의 '도구' 메뉴 또는 단축키.</summary>
    Toolbar,
}

/// <summary>모듈을 열 때 넘기는 대상. <see cref="Paths"/>는 우클릭한 항목(또는 도구 메뉴를 열 때의 선택), 비어 있을 수 있다.</summary>
/// <param name="Folder">활성 탭 폴더 (가상 폴더면 null).</param>
/// <param name="Action">우클릭 메뉴 항목이 모듈 안의 특정 동작을 고른 경우 그 키 (예: Direct Connect 기기 ID).</param>
public sealed record ModuleInvocation(IReadOnlyList<string> Paths, string? Folder, ModuleEntry Entry, string? Action = null);

/// <summary>
/// 켜고 끌 수 있는 도구 모듈. 처음 쓸 때 만들어지고, 모듈을 끄거나 앱을 닫으면 <see cref="IDisposable.Dispose"/>된다.
/// 화면(<see cref="View"/>)은 오른쪽 도구 패널과 별도 창 모두에 들어갈 수 있어야 하므로 자기가 어디에 있는지 가정하지 않는다.
/// </summary>
public interface IToolModule : IDisposable
{
    /// <summary>모듈 화면. 처음 접근할 때 만들고 이후 같은 인스턴스를 돌려준다.</summary>
    FrameworkElement View { get; }

    /// <summary>사용자가 모듈을 열었다 (화면이 이미 열려 있어도 호출된다 — 새 대상으로 갱신).</summary>
    void Open(ModuleInvocation invocation);

    /// <summary>
    /// 화면이 닫혔다. 캐시·작업자 같은 무거운 자원을 여기서 놓는다.
    /// <see cref="KeepsRunningWhenClosed"/>가 true면 모듈 인스턴스는 유지되고, false면 이어서 Dispose된다.
    /// </summary>
    void OnClosed();

    /// <summary>화면을 닫아도 계속 돌아야 하는 백그라운드 서비스가 있는지 (예: 파일 받기 대기).</summary>
    bool KeepsRunningWhenClosed { get; }

    /// <summary>
    /// 이미 실행 중인 모듈만 제공하는 동적 우클릭 항목 (예: 지금 발견된 기기 목록). 모듈이 실행 중이 아니면 호출되지 않는다.
    /// </summary>
    IReadOnlyList<ModuleMenuItem> GetDynamicMenuItems(IReadOnlyList<string> paths);

    /// <summary>작업 중이라 닫으면 안 되면 사용자에게 묻고 결과를 돌려준다 (true = 닫아도 됨).</summary>
    bool CanClose();
}

/// <summary>동적 우클릭 항목. <see cref="Action"/>은 <see cref="ModuleInvocation.Action"/>으로 돌아온다.</summary>
public sealed record ModuleMenuItem(string Text, string Action);

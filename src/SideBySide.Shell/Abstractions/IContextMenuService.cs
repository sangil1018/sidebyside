namespace SideBySide.Shell.Abstractions;

/// <summary>
/// 앱이 우클릭 메뉴 맨 위에 넣는 항목 (모듈 도구 등). <paramref name="Children"/>가 있으면 하위 메뉴가 되고 <paramref name="Invoke"/>는 무시된다.
/// </summary>
/// <param name="Glyph">Segoe Fluent 아이콘 글리프 (없으면 아이콘 없음).</param>
public sealed record ShellMenuItem(string Text, string? Glyph, Action? Invoke, IReadOnlyList<ShellMenuItem>? Children = null, bool Enabled = true);

/// <summary>Windows 원본 우클릭 메뉴.</summary>
public interface IContextMenuService
{
    /// <summary>
    /// 항목 메뉴. 같은 폴더 안의 항목들이어야 한다.
    /// <paramref name="interceptVerb"/>가 true를 돌려주면 앱이 직접 처리한 것으로 보고 셸 명령을 실행하지 않는다 (예: "rename").
    /// <paramref name="appItems"/>는 메뉴 맨 위에 구분선과 함께 넣는다.
    /// </summary>
    void ShowForItems(IReadOnlyList<string> paths, int screenX, int screenY, bool extended, Func<string, bool>? interceptVerb = null, IReadOnlyList<ShellMenuItem>? appItems = null);

    /// <summary>빈 공간(폴더 배경) 메뉴: 새로 만들기, 붙여넣기, 속성 등.</summary>
    void ShowForFolder(string folderPath, int screenX, int screenY, bool extended, Func<string, bool>? interceptVerb = null, IReadOnlyList<ShellMenuItem>? appItems = null);
}

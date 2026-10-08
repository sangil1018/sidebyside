namespace SideBySide.Shell.Abstractions;

/// <summary>Windows 원본 우클릭 메뉴.</summary>
public interface IContextMenuService
{
    /// <summary>
    /// 항목 메뉴. 같은 폴더 안의 항목들이어야 한다.
    /// <paramref name="interceptVerb"/>가 true를 돌려주면 앱이 직접 처리한 것으로 보고 셸 명령을 실행하지 않는다 (예: "rename").
    /// </summary>
    void ShowForItems(IReadOnlyList<string> paths, int screenX, int screenY, bool extended, Func<string, bool>? interceptVerb = null);

    /// <summary>빈 공간(폴더 배경) 메뉴: 새로 만들기, 붙여넣기, 속성 등.</summary>
    void ShowForFolder(string folderPath, int screenX, int screenY, bool extended, Func<string, bool>? interceptVerb = null);
}

using System.Windows.Input;

namespace SideBySide.App.Commands;

/// <summary>단축키가 동작하는 범위. 안쪽 범위가 먼저 처리된다 (파일 비교 화면 &gt; 파일 목록 &gt; 창 전체).</summary>
public enum CommandScope
{
    /// <summary>창 어디서든.</summary>
    Global,

    /// <summary>파일 목록에 포커스가 있을 때 (텍스트 입력 중에는 Ctrl+C 등이 입력란에 가도록).</summary>
    FileList,

    /// <summary>파일 비교 화면.</summary>
    Diff,
}

public sealed record CommandDefinition(
    string Id,
    string Title,
    Key Key,
    ModifierKeys Modifiers,
    CommandScope Scope,
    string? GestureText = null,
    bool IsAlternate = false)
{
    public string Gesture => GestureText ?? FormatGesture(Key, Modifiers);

    public string ToolTip => Gesture.Length > 0 ? $"{Title} ({Gesture})" : Title;

    private static string FormatGesture(Key key, ModifierKeys modifiers)
    {
        if (key == Key.None) return string.Empty;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(key switch
        {
            Key.Left => "←",
            Key.Right => "→",
            Key.Up => "↑",
            Key.Down => "↓",
            Key.Return => "Enter",
            Key.Delete => "Del",
            Key.Back => "Backspace",
            Key.Escape => "Esc",
            _ => key.ToString(),
        });
        return string.Join("+", parts);
    }
}

/// <summary>
/// 모든 명령과 단축키의 단일 정의 위치. 단축키를 바꾸려면 이 표만 고치면 된다
/// (키 바인딩, 버튼 툴팁, README 단축키 표가 모두 여기서 나온다).
/// </summary>
public static class CommandRegistry
{
    private const ModifierKeys None = ModifierKeys.None;
    private const ModifierKeys Ctrl = ModifierKeys.Control;
    private const ModifierKeys Shift = ModifierKeys.Shift;
    private const ModifierKeys Alt = ModifierKeys.Alt;
    private const ModifierKeys CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;

    public static IReadOnlyList<CommandDefinition> All { get; } =
    [
        // 모드·레이아웃
        new(CommandIds.ToggleDualPane, "한 창(탐색기) / 두 창(비교) 전환", Key.D, Ctrl, CommandScope.Global),
        new(CommandIds.ToggleDualPane, "한 창(탐색기) / 두 창(비교) 전환", Key.M, Ctrl, CommandScope.Global, IsAlternate: true),
        new(CommandIds.TogglePreview, "미리보기 창", Key.P, Alt, CommandScope.Global),
        new(CommandIds.ToggleSidebar, "탐색 창", Key.B, CtrlShift, CommandScope.Global),
        new(CommandIds.ToggleHidden, "숨김 항목 표시", Key.H, Ctrl, CommandScope.Global),
        new(CommandIds.ToggleTheme, "라이트 / 다크 테마", Key.T, CtrlShift, CommandScope.Global),
        new(CommandIds.SwitchPane, "왼쪽 / 오른쪽 창 전환", Key.Tab, None, CommandScope.FileList),
        new(CommandIds.ViewDetails, "자세히", Key.D6, CtrlShift, CommandScope.Global, "Ctrl+Shift+6"),
        new(CommandIds.ViewMediumIcons, "중간 아이콘", Key.D3, CtrlShift, CommandScope.Global, "Ctrl+Shift+3"),
        new(CommandIds.ViewLargeIcons, "큰 아이콘", Key.D2, CtrlShift, CommandScope.Global, "Ctrl+Shift+2"),

        // 탭
        new(CommandIds.NewTab, "새 탭", Key.T, Ctrl, CommandScope.Global),
        new(CommandIds.CloseTab, "탭 닫기", Key.W, Ctrl, CommandScope.Global),
        new(CommandIds.NextTab, "다음 탭", Key.Tab, Ctrl, CommandScope.Global),
        new(CommandIds.PrevTab, "이전 탭", Key.Tab, CtrlShift, CommandScope.Global),

        // 탐색
        new(CommandIds.Back, "뒤로", Key.Left, Alt, CommandScope.Global),
        new(CommandIds.Forward, "앞으로", Key.Right, Alt, CommandScope.Global),
        new(CommandIds.Up, "상위 폴더", Key.Up, Alt, CommandScope.Global),
        new(CommandIds.Up, "상위 폴더", Key.Back, None, CommandScope.FileList, IsAlternate: true),
        new(CommandIds.Refresh, "새로 고침 / 다시 비교", Key.F5, None, CommandScope.Global),
        new(CommandIds.FocusAddress, "주소 표시줄", Key.L, Ctrl, CommandScope.Global),
        new(CommandIds.FocusAddress, "주소 표시줄", Key.F4, None, CommandScope.Global, IsAlternate: true),
        new(CommandIds.FocusAddress, "주소 표시줄", Key.D, Alt, CommandScope.Global, IsAlternate: true),
        new(CommandIds.FocusSearch, "현재 폴더에서 검색", Key.F, Ctrl, CommandScope.Global),
        new(CommandIds.SearchSubfolders, "하위 폴더까지 검색", Key.None, None, CommandScope.Global, "검색란에서 Enter"),
        new(CommandIds.Cancel, "작업 취소", Key.Escape, None, CommandScope.Global),

        // 파일 작업
        new(CommandIds.Open, "열기", Key.Return, None, CommandScope.FileList),
        new(CommandIds.Rename, "이름 바꾸기", Key.F2, None, CommandScope.FileList),
        new(CommandIds.Undo, "실행 취소", Key.Z, Ctrl, CommandScope.FileList),
        new(CommandIds.Delete, "삭제 (휴지통)", Key.Delete, None, CommandScope.FileList),
        new(CommandIds.DeletePermanent, "영구 삭제", Key.Delete, Shift, CommandScope.FileList),
        new(CommandIds.Copy, "복사", Key.C, Ctrl, CommandScope.FileList),
        new(CommandIds.Cut, "잘라내기", Key.X, Ctrl, CommandScope.FileList),
        new(CommandIds.Paste, "붙여넣기", Key.V, Ctrl, CommandScope.FileList),
        new(CommandIds.NewFolder, "새 폴더", Key.N, CtrlShift, CommandScope.Global),
        new(CommandIds.NewTextFile, "새 텍스트 문서", Key.None, None, CommandScope.Global),
        new(CommandIds.Properties, "속성", Key.Return, Alt, CommandScope.FileList),
        new(CommandIds.CopyPath, "경로 복사", Key.C, CtrlShift, CommandScope.FileList),
        new(CommandIds.ShowContextMenu, "메뉴 열기", Key.F10, Shift, CommandScope.FileList),
        new(CommandIds.ShowContextMenu, "메뉴 열기", Key.Apps, None, CommandScope.FileList, IsAlternate: true),
        new(CommandIds.OpenTerminal, "여기서 터미널 열기", Key.Oem3, CtrlShift, CommandScope.Global, "Ctrl+Shift+`"),
        new(CommandIds.PinFavorite, "즐겨찾기에 고정", Key.None, None, CommandScope.Global),
        // Global: 목록 밖(도구 모음 클릭 후 등)에서도 동작. 입력란 안에서는 TextBox가 먼저 처리해 단어 이동 유지
        new(CommandIds.CopyToRight, "오른쪽 창으로 복사", Key.Right, Ctrl, CommandScope.Global),
        new(CommandIds.CopyToLeft, "왼쪽 창으로 복사", Key.Left, Ctrl, CommandScope.Global),
        new(CommandIds.EjectDrive, "USB 장치 안전하게 제거", Key.E, CtrlShift, CommandScope.Global),

        // 필터
        new(CommandIds.EditFilter, "필터", Key.F, CtrlShift, CommandScope.Global),
        new(CommandIds.ClearFilter, "필터 해제", Key.None, None, CommandScope.Global),

        // 비교
        new(CommandIds.NextDifference, "다음 차이", Key.F7, None, CommandScope.Global),
        new(CommandIds.PrevDifference, "이전 차이", Key.F7, Shift, CommandScope.Global),
        new(CommandIds.ToggleDifferencesOnly, "차이만 보기", Key.I, Ctrl, CommandScope.Global),
        new(CommandIds.CompareSelectedFiles, "선택한 파일 비교", Key.D, CtrlShift, CommandScope.Global),
        new(CommandIds.SaveSession, "비교 세션 저장", Key.S, CtrlShift, CommandScope.Global),
        new(CommandIds.OpenSession, "비교 세션 열기", Key.O, Ctrl, CommandScope.Global),

        // 파일 비교 화면
        new(CommandIds.DiffNext, "다음 차이", Key.F7, None, CommandScope.Diff),
        new(CommandIds.DiffPrev, "이전 차이", Key.F7, Shift, CommandScope.Diff),
        new(CommandIds.DiffApplyToRight, "현재 차이를 오른쪽에 적용", Key.Right, Alt, CommandScope.Diff),
        new(CommandIds.DiffApplyToLeft, "현재 차이를 왼쪽에 적용", Key.Left, Alt, CommandScope.Diff),
        new(CommandIds.DiffSave, "저장", Key.S, Ctrl, CommandScope.Diff),
        new(CommandIds.DiffUndo, "실행 취소", Key.Z, Ctrl, CommandScope.Diff),
        new(CommandIds.DiffToggleEdit, "편집 모드", Key.E, Ctrl, CommandScope.Diff),
        new(CommandIds.DiffClose, "비교 닫기", Key.Escape, None, CommandScope.Diff),
    ];

    private static readonly Dictionary<string, CommandDefinition> Primary =
        All.Where(d => !d.IsAlternate).ToDictionary(d => d.Id);

    public static CommandDefinition Get(string id) => Primary[id];
}

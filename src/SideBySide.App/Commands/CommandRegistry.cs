using System.Windows.Input;
using SideBySide.Core.Localization;

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

    /// <summary>키 입력이 아닌 다른 방식(예: "검색란에서 Enter")이라 단축키를 바꿀 수 없는 명령.</summary>
    public bool IsFixed => Key == Key.None && GestureText is not null;

    public string ToolTip => Gesture.Length > 0 ? $"{Title} ({Gesture})" : Title;

    public static string FormatGesture(Key key, ModifierKeys modifiers)
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
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.Oem3 => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.Oem2 => "/",
            Key.Oem5 => "\\",
            Key.Oem1 => ";",
            Key.Oem7 => "'",
            Key.OemOpenBrackets => "[",
            Key.Oem6 => "]",
            Key.D0 or Key.D1 or Key.D2 or Key.D3 or Key.D4 or Key.D5 or Key.D6 or Key.D7 or Key.D8 or Key.D9 => ((int)(key - Key.D0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
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

    /// <summary>기본 단축키 표. 사용자가 바꾼 값은 <see cref="ApplyOverrides"/>로 덮어쓴 <see cref="All"/>에 반영된다.</summary>
    public static IReadOnlyList<CommandDefinition> Defaults { get; } =
    [
        // 모드·레이아웃
        new(CommandIds.ToggleDualPane, L.T("한 창 / 두 창 전환"), Key.D, Ctrl, CommandScope.Global),
        new(CommandIds.ToggleCompare, L.T("두 창 비교 켜기/끄기"), Key.M, Ctrl, CommandScope.Global),
        new(CommandIds.TogglePreview, L.T("미리보기 창"), Key.P, Alt, CommandScope.Global),
        new(CommandIds.ToggleSidebar, L.T("탐색 창"), Key.B, CtrlShift, CommandScope.Global),
        new(CommandIds.ToggleHidden, L.T("숨김 항목 표시"), Key.H, Ctrl, CommandScope.Global),
        new(CommandIds.ToggleTheme, L.T("라이트 / 다크 테마"), Key.T, CtrlShift, CommandScope.Global),
        new(CommandIds.OpenSettings, L.T("환경설정"), Key.OemComma, Ctrl, CommandScope.Global),
        new(CommandIds.SwitchPane, L.T("왼쪽 / 오른쪽 창 전환"), Key.Tab, None, CommandScope.FileList),
        new(CommandIds.ViewDetails, L.T("자세히"), Key.D6, CtrlShift, CommandScope.Global, "Ctrl+Shift+6"),
        new(CommandIds.ViewMediumIcons, L.T("중간 아이콘"), Key.D3, CtrlShift, CommandScope.Global, "Ctrl+Shift+3"),
        new(CommandIds.ViewLargeIcons, L.T("큰 아이콘"), Key.D2, CtrlShift, CommandScope.Global, "Ctrl+Shift+2"),

        // 탭
        new(CommandIds.NewTab, L.T("새 탭"), Key.T, Ctrl, CommandScope.Global),
        new(CommandIds.CloseTab, L.T("탭 닫기"), Key.W, Ctrl, CommandScope.Global),
        new(CommandIds.NextTab, L.T("다음 탭"), Key.Tab, Ctrl, CommandScope.Global),
        new(CommandIds.PrevTab, L.T("이전 탭"), Key.Tab, CtrlShift, CommandScope.Global),

        // 탐색
        new(CommandIds.Back, L.T("뒤로"), Key.Left, Alt, CommandScope.Global),
        new(CommandIds.Forward, L.T("앞으로"), Key.Right, Alt, CommandScope.Global),
        new(CommandIds.Up, L.T("상위 폴더"), Key.Up, Alt, CommandScope.Global),
        new(CommandIds.Up, L.T("상위 폴더"), Key.Back, None, CommandScope.FileList, IsAlternate: true),
        new(CommandIds.Refresh, L.T("새로 고침 / 다시 비교"), Key.F5, None, CommandScope.Global),
        new(CommandIds.FocusAddress, L.T("주소 표시줄"), Key.L, Ctrl, CommandScope.Global),
        new(CommandIds.FocusAddress, L.T("주소 표시줄"), Key.F4, None, CommandScope.Global, IsAlternate: true),
        new(CommandIds.FocusAddress, L.T("주소 표시줄"), Key.D, Alt, CommandScope.Global, IsAlternate: true),
        new(CommandIds.FocusSearch, L.T("현재 폴더에서 검색"), Key.F, Ctrl, CommandScope.Global),
        new(CommandIds.SearchSubfolders, L.T("하위 폴더까지 검색"), Key.None, None, CommandScope.Global, L.T("검색란에서 Enter")),
        new(CommandIds.Cancel, L.T("작업 취소"), Key.Escape, None, CommandScope.Global),

        // 파일 작업
        new(CommandIds.Open, L.T("열기"), Key.Return, None, CommandScope.FileList),
        new(CommandIds.Rename, L.T("이름 바꾸기"), Key.F2, None, CommandScope.FileList),
        new(CommandIds.Undo, L.T("실행 취소"), Key.Z, Ctrl, CommandScope.FileList),
        new(CommandIds.Delete, L.T("삭제 (휴지통)"), Key.Delete, None, CommandScope.FileList),
        new(CommandIds.DeletePermanent, L.T("영구 삭제"), Key.Delete, Shift, CommandScope.FileList),
        new(CommandIds.Copy, L.T("복사"), Key.C, Ctrl, CommandScope.FileList),
        new(CommandIds.Cut, L.T("잘라내기"), Key.X, Ctrl, CommandScope.FileList),
        new(CommandIds.Paste, L.T("붙여넣기"), Key.V, Ctrl, CommandScope.FileList),
        new(CommandIds.NewFolder, L.T("새 폴더"), Key.N, CtrlShift, CommandScope.Global),
        new(CommandIds.NewTextFile, L.T("새 텍스트 문서"), Key.None, None, CommandScope.Global),
        new(CommandIds.Properties, L.T("속성"), Key.Return, Alt, CommandScope.FileList),
        new(CommandIds.CopyPath, L.T("경로 복사"), Key.C, CtrlShift, CommandScope.FileList),
        new(CommandIds.ShowContextMenu, L.T("메뉴 열기"), Key.F10, Shift, CommandScope.FileList),
        new(CommandIds.ShowContextMenu, L.T("메뉴 열기"), Key.Apps, None, CommandScope.FileList, IsAlternate: true),
        new(CommandIds.OpenTerminal, L.T("여기서 터미널 열기"), Key.Oem3, CtrlShift, CommandScope.Global, "Ctrl+Shift+`"),
        new(CommandIds.PinFavorite, L.T("즐겨찾기에 고정"), Key.None, None, CommandScope.Global),
        // Global: 목록 밖(도구 모음 클릭 후 등)에서도 동작. 입력란 안에서는 TextBox가 먼저 처리해 단어 이동 유지
        new(CommandIds.CopyToRight, L.T("오른쪽 창으로 복사"), Key.Right, Ctrl, CommandScope.Global),
        new(CommandIds.CopyToLeft, L.T("왼쪽 창으로 복사"), Key.Left, Ctrl, CommandScope.Global),
        new(CommandIds.MoveToRight, L.T("오른쪽 창으로 이동"), Key.Right, CtrlShift, CommandScope.Global),
        new(CommandIds.MoveToLeft, L.T("왼쪽 창으로 이동"), Key.Left, CtrlShift, CommandScope.Global),
        new(CommandIds.EjectDrive, L.T("USB 장치 안전하게 제거"), Key.E, CtrlShift, CommandScope.Global),

        // 필터
        new(CommandIds.EditFilter, L.T("필터"), Key.F, CtrlShift, CommandScope.Global),
        new(CommandIds.ClearFilter, L.T("필터 해제"), Key.None, None, CommandScope.Global),

        // 비교
        new(CommandIds.NextDifference, L.T("다음 차이"), Key.F7, None, CommandScope.Global),
        new(CommandIds.PrevDifference, L.T("이전 차이"), Key.F7, Shift, CommandScope.Global),
        new(CommandIds.ToggleDifferencesOnly, L.T("차이만 보기"), Key.I, Ctrl, CommandScope.Global),
        new(CommandIds.CompareSelectedFiles, L.T("선택한 파일 비교"), Key.D, CtrlShift, CommandScope.Global),
        new(CommandIds.SaveSession, L.T("비교 세션 저장"), Key.S, CtrlShift, CommandScope.Global),
        new(CommandIds.OpenSession, L.T("비교 세션 열기"), Key.O, Ctrl, CommandScope.Global),

        // 파일 비교 화면
        new(CommandIds.DiffNext, L.T("다음 차이"), Key.F7, None, CommandScope.Diff),
        new(CommandIds.DiffPrev, L.T("이전 차이"), Key.F7, Shift, CommandScope.Diff),
        new(CommandIds.DiffApplyToRight, L.T("현재 차이를 오른쪽에 적용"), Key.Right, Alt, CommandScope.Diff),
        new(CommandIds.DiffApplyToLeft, L.T("현재 차이를 왼쪽에 적용"), Key.Left, Alt, CommandScope.Diff),
        new(CommandIds.DiffSave, L.T("저장"), Key.S, Ctrl, CommandScope.Diff),
        new(CommandIds.DiffUndo, L.T("실행 취소"), Key.Z, Ctrl, CommandScope.Diff),
        new(CommandIds.DiffToggleEdit, L.T("편집 모드"), Key.E, Ctrl, CommandScope.Diff),
        new(CommandIds.DiffClose, L.T("비교 닫기"), Key.Escape, None, CommandScope.Diff),

        // 도구 모듈 (켜진 모듈만 실행됨, 기본 단축키 없음)
        .. Modules.ModuleCatalog.All.Select(m => new CommandDefinition(m.CommandId, L.F("도구: {0}", m.Title), Key.None, None, CommandScope.Global)),
    ];

    private static Dictionary<string, CommandDefinition> _primary = Defaults.Where(d => !d.IsAlternate).ToDictionary(d => d.Id);

    /// <summary>현재 적용 중인 단축키 표 (기본값 + 사용자 변경).</summary>
    public static IReadOnlyList<CommandDefinition> All { get; private set; } = Defaults;

    public static CommandDefinition Get(string id) => _primary[id];

    public static CommandDefinition DefaultOf(string id) => Defaults.First(d => d.Id == id && !d.IsAlternate);

    /// <summary>
    /// 사용자 단축키 적용. 바꾼 명령은 기본 단축키와 보조 단축키를 모두 새 키 하나로 대체한다 (빈 값 = 단축키 없음).
    /// 해석할 수 없는 값은 무시하고 기본값을 쓴다.
    /// </summary>
    public static void ApplyOverrides(IReadOnlyDictionary<string, string> overrides)
    {
        var result = Compose(overrides);
        All = result;
        _primary = result.Where(d => !d.IsAlternate).ToDictionary(d => d.Id);
    }

    /// <summary>기본 표에 사용자 단축키를 덮어쓴 결과 (적용하지 않고 미리 보기·충돌 검사에 쓴다).</summary>
    public static List<CommandDefinition> Compose(IReadOnlyDictionary<string, string> overrides)
    {
        var result = new List<CommandDefinition>();
        foreach (var def in Defaults)
        {
            if (def.IsFixed || !overrides.TryGetValue(def.Id, out var text) || !GestureCodec.TryParse(text, out var key, out var modifiers))
            {
                result.Add(def);
                continue;
            }

            if (def.IsAlternate) continue;
            result.Add(def with { Key = key, Modifiers = modifiers, GestureText = null });
        }

        return result;
    }

    /// <summary>두 범위의 단축키가 서로 가리는지 (같은 범위이거나 한쪽이 창 전체).</summary>
    public static bool ScopesOverlap(CommandScope a, CommandScope b) => a == b || a == CommandScope.Global || b == CommandScope.Global;
}

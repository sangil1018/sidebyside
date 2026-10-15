using SideBySide.Core.Settings;

namespace SideBySide.App.Services;

/// <summary>환경설정 창에서 편집하는 값.</summary>
/// <param name="ShortcutWhenClosed">앱이 꺼져 있어도 단축키 사용 (로그인 시 백그라운드 실행).</param>
/// <param name="KeyBindings">사용자가 바꾼 단축키 (명령 ID → "Ctrl+Shift+E", 빈 값 = 없음).</param>
public sealed record AppPreferences(
    ThemeMode Theme,
    AppLanguage Language,
    bool DefaultFileManager,
    OpenShortcut OpenShortcut,
    bool ShortcutWhenClosed,
    IReadOnlyList<SystemPlace> SystemPlaces,
    IReadOnlyDictionary<string, string> KeyBindings);

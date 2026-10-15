using System.Windows.Input;

namespace SideBySide.App.Commands;

/// <summary>설정 파일에 저장하는 단축키 문자열("Ctrl+Shift+Right", 빈 문자열 = 없음)과 키 조합 사이 변환.</summary>
public static class GestureCodec
{
    public static string Format(Key key, ModifierKeys modifiers)
    {
        if (key == Key.None) return string.Empty;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;
        if (text is null) return false;
        if (text.Length == 0) return true; // 단축키 없음

        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "SHIFT":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "ALT":
                    modifiers |= ModifierKeys.Alt;
                    break;
                default:
                    if (!Enum.TryParse(part, ignoreCase: true, out key)) return false;
                    break;
            }
        }

        return key != Key.None && IsValid(key, modifiers);
    }

    /// <summary>WPF가 받아들이는 조합인지 (Ctrl·Alt 없이 글자·숫자 키만 누르는 조합은 입력과 겹쳐 쓸 수 없다).</summary>
    public static bool IsValid(Key key, ModifierKeys modifiers)
    {
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System)
            return false;
        try
        {
            _ = new KeyGesture(key, modifiers);
            return true;
        }
        catch (NotSupportedException)
        {
            // Tab·Enter·Esc·Backspace처럼 KeyGesture가 거부하지만 KeyBinding으로는 쓰는 키
            return modifiers == ModifierKeys.None && key is Key.Tab or Key.Return or Key.Escape or Key.Back or Key.Space or Key.Apps;
        }
    }
}

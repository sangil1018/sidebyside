using SideBySide.Shell.Interop;

namespace SideBySide.Shell.Services;

/// <summary>
/// 앱 전역 단축키(RegisterHotKey). 앱이 실행 중일 때만 동작하고, 다른 프로그램이 같은 키를 쓰면 등록에 실패한다.
/// 누르면 창 핸들로 WM_HOTKEY가 오고, wParam은 <see cref="Id"/>다.
/// </summary>
public static class GlobalHotkey
{
    public const int Id = 0x5342;

    public const uint ModAlt = 0x1;
    public const uint ModControl = 0x2;
    public const uint ModShift = 0x4;
    public const uint ModWin = 0x8;
    public const uint VkE = 0x45;

    private const uint ModNoRepeat = 0x4000;

    public static bool Register(IntPtr hwnd, uint modifiers, uint virtualKey)
        => NativeMethods.RegisterHotKey(hwnd, Id, modifiers | ModNoRepeat, virtualKey);

    public static void Unregister(IntPtr hwnd) => NativeMethods.UnregisterHotKey(hwnd, Id);

    /// <summary>다른 프로세스의 창을 복원해 앞으로 가져온다 (단축키를 받은 프로세스는 포그라운드를 넘겨줄 수 있다).</summary>
    public static void BringToFront(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(hwnd);
    }
}

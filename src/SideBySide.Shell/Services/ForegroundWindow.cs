using static SideBySide.Shell.Interop.NativeMethods;

namespace SideBySide.Shell.Services;

/// <summary>
/// 전경 창(사용자 입력을 받는 창) 조회·되돌리기. UI 자동화로 띄운 인스턴스가 활성화됐을 때
/// 사용자가 쓰던 창으로 전경을 즉시 돌려주는 데 쓴다.
/// </summary>
public static class ForegroundWindow
{
    /// <summary>지금 전경 창이 다른 프로세스의 것이면 그 창, 아니면 0.</summary>
    public static IntPtr OtherProcessForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId ? IntPtr.Zero : hwnd;
    }

    /// <summary>이 프로세스의 창이 전경인지.</summary>
    public static bool IsOwnProcessForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>그 창이 아직 있으면 전경으로 되돌린다 (이 프로세스가 지금 전경일 때만 허용된다).</summary>
    public static bool Restore(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd) && SetForegroundWindow(hwnd);
}

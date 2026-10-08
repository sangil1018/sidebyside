using static SideBySide.Shell.Interop.NativeMethods;

namespace SideBySide.Shell.Services;

/// <summary>DWM 창 속성: 어두운 제목 표시줄, Windows 11 둥근 모서리.</summary>
public static class WindowTheming
{
    public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        var value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref value, sizeof(int));
    }

    public static void SetRoundCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }
}

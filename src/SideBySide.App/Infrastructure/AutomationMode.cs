using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SideBySide.Core;
using SideBySide.Shell.Services;

namespace SideBySide.App.Infrastructure;

/// <summary>
/// UI 자동화(테스트 하네스·UIA 드라이버)로 띄운 인스턴스의 동작 (<see cref="AppPaths.AutomationVariable"/>).
/// 사용자가 PC를 쓰는 중에도 방해하지 않도록 창을 화면 밖에 포커스 없이 띄우고, 전역 단축키를 잡지 않는다.
/// </summary>
internal static class AutomationMode
{
    private const double OffscreenOrigin = -20000;
    private const int WM_ACTIVATE = 0x0006;
    private const int WM_ACTIVATEAPP = 0x001C;
    private const int WA_INACTIVE = 0;

    private static int _windows;
    private static DispatcherTimer? _tracker;

    /// <summary>마지막으로 본 다른 프로그램의 전경 창 (사용자가 쓰던 창).</summary>
    private static IntPtr _userForeground;

    public static bool IsOn { get; } = AppPaths.IsAutomation;

    /// <summary>
    /// 창을 화면 밖에, 활성화·작업 표시줄 표시 없이 띄우도록 설정한다. 렌더링·레이아웃·UI Automation은 그대로 동작한다.
    /// <para>
    /// UI Automation 패턴(Invoke·SetValue·Select)을 부르면 Windows가 대상 앱에 전경 권한을 주어 창이 활성화될 수 있다.
    /// 그래서 이 창이 활성화되면 사용자가 쓰던 창으로 전경을 곧바로 돌려준다 (그 순간에는 이 프로세스가 전경이라 허용된다).
    /// </para>
    /// </summary>
    public static void PlaceOffscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.WindowState = WindowState.Normal;
        window.Left = OffscreenOrigin - 40 * _windows;
        window.Top = OffscreenOrigin;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(GiveBackActivation);
        StartTracking(window.Dispatcher);
        _windows++;
    }

    /// <summary>자동화 모드가 아닐 때만 창을 앞으로 가져온다 (자동화 중에는 사용자 창의 포커스를 빼앗지 않는다).</summary>
    public static void Activate(Window? window)
    {
        if (!IsOn) window?.Activate();
    }

    /// <summary>
    /// 사용자가 지금 쓰는 (다른 프로그램의) 전경 창을 짧은 주기로 기억해 두고, 혹시 이 앱이 전경을 차지하고 있으면 돌려준다
    /// (활성화 메시지 없이 전경이 넘어오는 경우까지 잡는 안전망).
    /// </summary>
    private static void StartTracking(Dispatcher dispatcher)
    {
        if (_tracker is not null) return;
        Check();
        _tracker = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Normal, (_, _) => Check(), dispatcher);
        _tracker.Start();
    }

    private static void Check()
    {
        var other = ForegroundWindow.OtherProcessForeground();
        if (other != IntPtr.Zero) _userForeground = other;
        else if (ForegroundWindow.IsOwnProcessForeground()) GiveBack(IntPtr.Zero, "주기 확인");
    }

    private static IntPtr GiveBackActivation(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_ACTIVATE: 이 창이 활성화됨 (lParam = 비활성화되는 창). WM_ACTIVATEAPP: 앱 단위로 전경이 넘어옴
        // (창이 이미 앱 안에서 활성 상태였으면 WM_ACTIVATE 없이 이것만 온다).
        if (msg == WM_ACTIVATE && (wParam.ToInt64() & 0xFFFF) != WA_INACTIVE) GiveBack(lParam, "WM_ACTIVATE");
        else if (msg == WM_ACTIVATEAPP && wParam != IntPtr.Zero) GiveBack(IntPtr.Zero, "WM_ACTIVATEAPP");
        return IntPtr.Zero;
    }

    /// <summary>사용자가 쓰던 창으로 전경을 돌려준다 (비활성화되는 창을 알면 그 창, 아니면 마지막으로 기억한 창).</summary>
    private static void GiveBack(IntPtr previous, string trigger)
    {
        if (!ForegroundWindow.IsOwnProcessForeground() && trigger != "WM_ACTIVATE") return;
        var restored = ForegroundWindow.Restore(previous) || ForegroundWindow.Restore(_userForeground);
        Diag.Note($"[automation] 전경을 가져옴({trigger}) → " + (restored ? "사용자 창으로 돌려줌" : "돌려줄 사용자 창을 찾지 못함"));
    }
}

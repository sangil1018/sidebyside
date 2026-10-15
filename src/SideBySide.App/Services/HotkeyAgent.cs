using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using SideBySide.Core.Settings;
using SideBySide.Shell.Services;
using SideBySide.Core.Localization;

namespace SideBySide.App.Services;

/// <summary>
/// 앱이 꺼져 있어도 창 열기 단축키가 동작하도록 Windows 로그인 시 창 없이 실행되는 작은 프로세스(<c>SideBySide.exe --hotkey-agent</c>).
/// 단축키를 누르면 열려 있는 SideBySide 창을 앞으로 가져오고, 없으면 새로 실행한다.
/// 같은 키는 한 곳만 등록할 수 있으므로, 이 기능이 켜져 있으면 메인 창은 단축키를 직접 등록하지 않는다.
/// </summary>
public static class HotkeyAgent
{
    public const string Argument = "--hotkey-agent";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "SideBySide Hotkey";
    private const string MutexName = @"Local\SideBySide.HotkeyAgent";
    private const string ExitEventName = @"Local\SideBySide.HotkeyAgent.Exit";
    private const int WM_HOTKEY = 0x0312;

    /// <summary>로그인 시 자동 실행이 등록되어 있는지.</summary>
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
    }

    public static bool IsRunning
    {
        get
        {
            if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
    }

    public static (uint Modifiers, uint Key) KeysFor(OpenShortcut shortcut) => shortcut switch
    {
        OpenShortcut.WinCtrlE => (GlobalHotkey.ModWin | GlobalHotkey.ModControl, GlobalHotkey.VkE),
        OpenShortcut.WinShiftE => (GlobalHotkey.ModWin | GlobalHotkey.ModShift, GlobalHotkey.VkE),
        _ => (GlobalHotkey.ModWin | GlobalHotkey.ModAlt, GlobalHotkey.VkE),
    };

    /// <summary>로그인 시 자동 실행을 등록하고, 이미 떠 있는 에이전트는 종료한다. 실행은 <see cref="EnsureRunning"/>.</summary>
    public static void Enable(OpenShortcut shortcut)
    {
        Stop();
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(RunValue, $"\"{ExecutablePath}\" {Argument} {shortcut}", RegistryValueKind.String);
    }

    /// <summary>자동 실행을 지우고 떠 있는 에이전트를 종료한다.</summary>
    public static void Disable()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            key?.DeleteValue(RunValue, throwOnMissingValue: false);
        Stop();
    }

    public static void EnsureRunning(OpenShortcut shortcut)
    {
        if (IsRunning) return;
        Process.Start(new ProcessStartInfo(ExecutablePath, $"{Argument} {shortcut}") { UseShellExecute = false })?.Dispose();
    }

    public static bool TryParse(IReadOnlyList<string> args, out OpenShortcut shortcut)
    {
        shortcut = OpenShortcut.WinAltE;
        if (args.Count == 0 || args[0] != Argument) return false;
        if (args.Count > 1 && Enum.TryParse(args[1], out OpenShortcut parsed)) shortcut = parsed;
        return true;
    }

    /// <summary>에이전트로 실행: 창 없이 단축키만 등록하고 종료 신호를 기다린다. 이미 떠 있거나 키를 등록할 수 없으면 바로 끝낸다.</summary>
    public static void Run(Application app, OpenShortcut shortcut)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            app.Shutdown();
            return;
        }

        var source = new HwndSource(new HwndSourceParameters("SideBySideHotkeyAgent") { ParentWindow = new IntPtr(-3) }); // HWND_MESSAGE
        var (modifiers, key) = KeysFor(shortcut);
        if (!GlobalHotkey.Register(source.Handle, modifiers, key))
        {
            Quit();
            return;
        }

        source.AddHook(Hook);
        var exit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        ThreadPool.RegisterWaitForSingleObject(exit, (_, _) => app.Dispatcher.BeginInvoke(Quit), null, Timeout.Infinite, executeOnlyOnce: true);

        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == GlobalHotkey.Id)
            {
                OpenOrActivate();
                handled = true;
            }

            return IntPtr.Zero;
        }

        void Quit()
        {
            // 단축키를 먼저 풀고 뮤텍스를 놓아야, 기다리던 쪽(설정 변경)이 같은 키를 바로 등록할 수 있다
            GlobalHotkey.Unregister(source.Handle);
            source.Dispose();
            mutex.ReleaseMutex();
            mutex.Dispose();
            app.Shutdown();
        }
    }

    /// <summary>떠 있는 에이전트에 종료를 알리고 끝날 때까지 잠깐 기다린다.</summary>
    private static void Stop()
    {
        if (EventWaitHandle.TryOpenExisting(ExitEventName, out var exit))
        {
            using (exit) exit.Set();
        }

        for (var i = 0; i < 30 && IsRunning; i++) Thread.Sleep(100);
    }

    private static void OpenOrActivate()
    {
        using var self = Process.GetCurrentProcess();
        var others = Process.GetProcessesByName(self.ProcessName);
        try
        {
            var window = others.Where(p => p.Id != self.Id).Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
            if (window != IntPtr.Zero) GlobalHotkey.BringToFront(window);
            else Process.Start(new ProcessStartInfo(ExecutablePath) { UseShellExecute = false })?.Dispose();
        }
        finally
        {
            foreach (var p in others) p.Dispose();
        }
    }

    private static string ExecutablePath => Environment.ProcessPath ?? throw new InvalidOperationException(L.T("실행 파일 경로를 찾을 수 없습니다."));
}

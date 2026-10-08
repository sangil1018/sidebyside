using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SideBySide.Shell.Abstractions;
using static SideBySide.Shell.Interop.NativeMethods;

namespace SideBySide.Shell.Services;

public sealed class ShellLauncher(IOwnerWindow owner) : ILauncher
{
    public void Open(string path)
    {
        var psi = new ProcessStartInfo(path) { UseShellExecute = true };
        var dir = Path.GetDirectoryName(path);
        if (dir is not null) psi.WorkingDirectory = dir;
        Process.Start(psi);
    }

    public void OpenWith(string path)
        => Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}") { UseShellExecute = false });

    public void ShowProperties(string path)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_INVOKEIDLIST,
            hwnd = owner.Handle,
            lpVerb = "properties",
            lpFile = path,
            nShow = SW_SHOW,
        };
        ShellExecuteEx(ref info);
    }

    public void OpenTerminal(string directory)
    {
        // "C:\" 처럼 역슬래시로 끝나면 따옴표가 이스케이프되므로 "C:\." 로 넘긴다.
        var arg = directory.EndsWith('\\') ? directory + "." : directory;
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{arg}\"") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = true, WorkingDirectory = directory });
        }
    }

    public void RevealInExplorer(string path)
        => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
}

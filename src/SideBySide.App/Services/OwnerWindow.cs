using System.Windows;
using System.Windows.Interop;
using SideBySide.Shell.Abstractions;

namespace SideBySide.App.Services;

/// <summary>메인 창 핸들 제공 (셸 대화상자 소유자).</summary>
public sealed class OwnerWindow : IOwnerWindow
{
    public IntPtr Handle
    {
        get
        {
            var window = Application.Current?.MainWindow;
            return window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
        }
    }
}

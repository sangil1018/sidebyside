using System.Runtime.InteropServices;
using System.Windows.Interop;
using SideBySide.Shell.Abstractions;
using SideBySide.Shell.Interop;
using static SideBySide.Shell.Interop.NativeMethods;

namespace SideBySide.Shell.Services;

/// <summary>
/// IContextMenu로 Windows 원본 우클릭 메뉴를 띄운다 (설치된 확장 메뉴, 보내기, 연결 프로그램 등 포함).
/// 소유 창의 메시지를 IContextMenu2/3에 전달해야 하위 메뉴(보내기/연결 프로그램)가 제대로 그려진다.
/// </summary>
public sealed class ShellContextMenu(IOwnerWindow owner) : IContextMenuService
{
    private const uint FirstCommand = 1;
    private const uint LastCommand = 0x7FFF;

    private IContextMenu2? _menu2;
    private IContextMenu3? _menu3;

    public void ShowForItems(IReadOnlyList<string> paths, int screenX, int screenY, bool extended, Func<string, bool>? interceptVerb = null)
    {
        if (paths.Count == 0) return;

        var pidls = new List<IntPtr>();
        IntPtr parentPtr = IntPtr.Zero, menuPtr = IntPtr.Zero;
        try
        {
            foreach (var path in paths)
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
                    pidls.Add(pidl);
            }

            if (pidls.Count == 0) return;

            var iidShellFolder = typeof(IShellFolder).GUID;
            var children = new IntPtr[pidls.Count];
            for (int i = 0; i < pidls.Count; i++)
            {
                if (SHBindToParent(pidls[i], ref iidShellFolder, out var folderPtr, out children[i]) != 0) return;
                if (i == 0) parentPtr = folderPtr;
                else Marshal.Release(folderPtr);
            }

            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
            var iidMenu = typeof(IContextMenu).GUID;
            if (folder.GetUIObjectOf(owner.Handle, (uint)children.Length, children, ref iidMenu, IntPtr.Zero, out menuPtr) != 0) return;

            Show(menuPtr, screenX, screenY, extended, interceptVerb);
        }
        finally
        {
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
            foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
        }
    }

    public void ShowForFolder(string folderPath, int screenX, int screenY, bool extended, Func<string, bool>? interceptVerb = null)
    {
        IntPtr pidl = IntPtr.Zero, folderPtr = IntPtr.Zero, menuPtr = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(folderPath, IntPtr.Zero, out pidl, 0, out _) != 0) return;
            var iidShellFolder = typeof(IShellFolder).GUID;
            if (SHBindToObject(IntPtr.Zero, pidl, IntPtr.Zero, ref iidShellFolder, out folderPtr) != 0) return;

            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            var iidMenu = typeof(IContextMenu).GUID;
            if (folder.CreateViewObject(owner.Handle, ref iidMenu, out menuPtr) != 0) return;

            Show(menuPtr, screenX, screenY, extended, interceptVerb);
        }
        finally
        {
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (folderPtr != IntPtr.Zero) Marshal.Release(folderPtr);
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    private void Show(IntPtr menuPtr, int x, int y, bool extended, Func<string, bool>? interceptVerb)
    {
        var menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
        _menu2 = menu as IContextMenu2;
        _menu3 = menu as IContextMenu3;

        var hMenu = CreatePopupMenu();
        var source = HwndSource.FromHwnd(owner.Handle);
        try
        {
            var flags = CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME | (extended ? CMF_EXTENDEDVERBS : 0);
            if (menu.QueryContextMenu(hMenu, 0, FirstCommand, LastCommand, flags) < 0) return;

            source?.AddHook(WndProc);
            var command = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, x, y, owner.Handle, IntPtr.Zero);
            source?.RemoveHook(WndProc);
            if (command < FirstCommand) return;

            var offset = command - FirstCommand;
            var verb = GetVerb(menu, offset);
            if (verb is not null && interceptVerb?.Invoke(verb) == true) return;

            var invoke = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE
                        | (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? CMIC_MASK_SHIFT_DOWN : 0)
                        | (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) ? CMIC_MASK_CONTROL_DOWN : 0),
                hwnd = owner.Handle,
                lpVerb = (IntPtr)offset,
                lpVerbW = (IntPtr)offset,
                nShow = SW_SHOW,
                ptInvoke = new POINT { X = x, Y = y },
            };
            menu.InvokeCommand(ref invoke);
        }
        finally
        {
            DestroyMenu(hMenu);
            _menu2 = null;
            _menu3 = null;
            Marshal.ReleaseComObject(menu);
        }
    }

    private static string? GetVerb(IContextMenu menu, uint offset)
    {
        const int capacity = 256;
        var buffer = Marshal.AllocHGlobal(capacity * 2);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            return menu.GetCommandString((UIntPtr)offset, GCS_VERBW, IntPtr.Zero, buffer, capacity) == 0
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is not (WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)) return IntPtr.Zero;

        if (_menu3 is not null && _menu3.HandleMenuMsg2((uint)msg, wParam, lParam, out var result) == 0)
        {
            handled = true;
            return result;
        }

        if (_menu2 is not null && _menu2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
        {
            handled = true;
        }

        return IntPtr.Zero;
    }
}

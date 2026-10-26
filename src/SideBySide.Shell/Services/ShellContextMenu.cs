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

    public void ShowForItems(IReadOnlyList<string> paths, int screenX, int screenY, bool extended, Func<string, bool>? interceptVerb = null, IReadOnlyList<ShellMenuItem>? appItems = null)
    {
        Action? appAction = null;
        WithItemMenu(owner.Handle, paths, menuPtr => appAction = Show(menuPtr, screenX, screenY, extended, interceptVerb, appItems));

        // 앱 항목은 셸 메뉴·COM 개체를 모두 정리한 뒤 실행 (모듈 창을 띄워도 메뉴가 남아 있지 않게)
        appAction?.Invoke();
    }

    /// <summary>
    /// 메뉴를 띄우지 않고 셸 동사(예: "eject")를 실행. 셸 확장이 오래 걸릴 수 있어 별도 STA 스레드에서 실행.
    /// 반환: 동사를 찾아 실행했으면 true.
    /// </summary>
    public static Task<bool> InvokeVerbAsync(string path, string verb)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var ok = false;
            try
            {
                WithItemMenu(IntPtr.Zero, [path], menuPtr =>
                {
                    var menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
                    var hMenu = CreatePopupMenu();
                    var verbA = Marshal.StringToHGlobalAnsi(verb);
                    var verbW = Marshal.StringToHGlobalUni(verb);
                    try
                    {
                        // 일부 셸 확장은 메뉴를 먼저 구성해야 동사를 인식한다
                        menu.QueryContextMenu(hMenu, 0, FirstCommand, LastCommand, CMF_NORMAL);
                        var invoke = new CMINVOKECOMMANDINFOEX
                        {
                            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                            fMask = CMIC_MASK_UNICODE,
                            lpVerb = verbA,
                            lpVerbW = verbW,
                            nShow = SW_SHOW,
                        };
                        ok = menu.InvokeCommand(ref invoke) == 0;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(verbA);
                        Marshal.FreeHGlobal(verbW);
                        DestroyMenu(hMenu);
                        Marshal.ReleaseComObject(menu);
                    }
                });
            }
            catch (COMException)
            {
            }

            tcs.SetResult(ok);
        })
        { IsBackground = true, Name = "ShellVerb" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    /// <summary>같은 폴더 안 항목들의 IContextMenu를 만들어 <paramref name="use"/>에 넘기고 정리한다.</summary>
    private static void WithItemMenu(IntPtr ownerHandle, IReadOnlyList<string> paths, Action<IntPtr> use)
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
            if (folder.GetUIObjectOf(ownerHandle, (uint)children.Length, children, ref iidMenu, IntPtr.Zero, out menuPtr) != 0) return;

            use(menuPtr);
        }
        finally
        {
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
            foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
        }
    }

    public void ShowForFolder(string folderPath, int screenX, int screenY, bool extended, Func<string, bool>? interceptVerb = null, IReadOnlyList<ShellMenuItem>? appItems = null)
    {
        IntPtr pidl = IntPtr.Zero, folderPtr = IntPtr.Zero, menuPtr = IntPtr.Zero;
        Action? appAction = null;
        try
        {
            if (SHParseDisplayName(folderPath, IntPtr.Zero, out pidl, 0, out _) != 0) return;
            var iidShellFolder = typeof(IShellFolder).GUID;
            if (SHBindToObject(IntPtr.Zero, pidl, IntPtr.Zero, ref iidShellFolder, out folderPtr) != 0) return;

            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            var iidMenu = typeof(IContextMenu).GUID;
            if (folder.CreateViewObject(owner.Handle, ref iidMenu, out menuPtr) != 0) return;

            appAction = Show(menuPtr, screenX, screenY, extended, interceptVerb, appItems);
        }
        finally
        {
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (folderPtr != IntPtr.Zero) Marshal.Release(folderPtr);
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }

        appAction?.Invoke();
    }

    /// <summary>메뉴를 띄우고 셸 명령이면 바로 실행한다. 앱 항목을 고르면 그 동작을 돌려준다 (호출자가 정리 후 실행).</summary>
    private Action? Show(IntPtr menuPtr, int x, int y, bool extended, Func<string, bool>? interceptVerb, IReadOnlyList<ShellMenuItem>? appItems)
    {
        var menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
        _menu2 = menu as IContextMenu2;
        _menu3 = menu as IContextMenu3;

        var hMenu = CreatePopupMenu();
        var source = HwndSource.FromHwnd(owner.Handle);
        using var app = new AppMenuItems(owner.Handle);
        try
        {
            var flags = CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME | (extended ? CMF_EXTENDEDVERBS : 0);
            if (menu.QueryContextMenu(hMenu, 0, FirstCommand, LastCommand, flags) < 0) return null;
            if (appItems is { Count: > 0 }) app.InsertAtTop(hMenu, appItems);

            source?.AddHook(WndProc);
            var command = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, x, y, owner.Handle, IntPtr.Zero);
            source?.RemoveHook(WndProc);
            if (app.TryGetAction(command, out var appAction)) return appAction;
            if (command < FirstCommand || command > LastCommand) return null;

            var offset = command - FirstCommand;
            var verb = GetVerb(menu, offset);
            if (verb is not null && interceptVerb?.Invoke(verb) == true) return null;

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
            return null;
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

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SideBySide.Shell.Interop;
using static SideBySide.Shell.Interop.NativeMethods;

namespace SideBySide.Shell.Preview;

/// <summary>
/// Windows 미리보기 처리기(PDF, Office, 동영상 등)를 WPF 안에 호스팅하는 컨트롤.
/// 처리기가 없거나 실패하면 <see cref="Open"/>이 false를 돌려주고 호출 측이 대체 표시를 한다.
/// </summary>
public sealed class PreviewHandlerHost : HwndHost
{
    private const uint StgmRead = 0;

    private IntPtr _hwnd;
    private object? _handler;
    private Stream? _stream;

    public bool Open(string path)
    {
        Unload();
        if (_hwnd == IntPtr.Zero) return false;
        if (!PreviewHandlerRegistry.TryFind(Path.GetExtension(path), out var clsid)) return false;

        try
        {
            var type = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
            _handler = Activator.CreateInstance(type);

            if (_handler is IInitializeWithFile withFile)
            {
                withFile.Initialize(path, StgmRead);
            }
            else if (_handler is IInitializeWithStream withStream)
            {
                _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                withStream.Initialize(new ComStream(_stream), StgmRead);
            }
            else
            {
                Unload();
                return false;
            }

            var handler = (IPreviewHandler)_handler!;
            GetClientRect(_hwnd, out var rect);
            handler.SetWindow(_hwnd, ref rect);
            handler.DoPreview();
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException or UnauthorizedAccessException or TypeLoadException or ArgumentException)
        {
            Unload();
            return false;
        }
    }

    public void Unload()
    {
        if (_handler is IPreviewHandler handler)
        {
            try { handler.Unload(); }
            catch (COMException) { }
        }

        if (_handler is not null && Marshal.IsComObject(_handler)) Marshal.FinalReleaseComObject(_handler);
        _handler = null;
        _stream?.Dispose();
        _stream = null;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowEx(0, "static", string.Empty, WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Unload();
        DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (_handler is IPreviewHandler handler && GetClientRect(_hwnd, out var rect))
        {
            try { handler.SetRect(ref rect); }
            catch (COMException) { }
        }
    }
}

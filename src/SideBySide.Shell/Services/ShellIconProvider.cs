using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SideBySide.Shell.Abstractions;
using static SideBySide.Shell.Interop.NativeMethods;

namespace SideBySide.Shell.Services;

/// <summary>
/// 시스템 아이콘 제공자.
/// - 일반 파일은 확장자 단위로 캐시 (디스크 접근 없이 USEFILEATTRIBUTES 사용 → 매우 빠름).
/// - exe/lnk/ico처럼 파일마다 아이콘이 다른 형식과 드라이브는 경로 단위로 캐시.
/// - 추출은 전용 STA 스레드 하나에서 수행 (셸 API는 COM 초기화된 스레드 필요). UI 스레드는 막지 않는다.
/// </summary>
public sealed class ShellIconProvider : IIconProvider, IDisposable
{
    private static readonly HashSet<string> PerFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".url", ".cur", ".ani", ".scr", ".msc", ".appref-ms", ".cpl",
    };

    private readonly ConcurrentDictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _typeNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<(string Path, bool IsDir, Action<ImageSource?> Callback)> _queue = new();
    private readonly Thread _worker;

    public ShellIconProvider()
    {
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "IconLoader", Priority = ThreadPriority.BelowNormal };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    public ImageSource? TryGetCached(string path, bool isDirectory)
        => _icons.TryGetValue(CacheKey(path, isDirectory), out var icon) ? icon : null;

    public void Request(string path, bool isDirectory, Action<ImageSource?> callback)
    {
        if (_icons.TryGetValue(CacheKey(path, isDirectory), out var icon))
        {
            callback(icon);
            return;
        }

        if (!_queue.IsAddingCompleted) _queue.Add((path, isDirectory, callback));
    }

    public string GetTypeName(string path, bool isDirectory)
    {
        var key = isDirectory ? "<dir>" : System.IO.Path.GetExtension(path);
        return _typeNames.GetOrAdd(key, _ =>
        {
            var info = default(SHFILEINFO);
            var attrs = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            SHGetFileInfo(isDirectory ? "folder" : "file" + key, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
            return string.IsNullOrEmpty(info.szTypeName) ? (key.Length > 1 ? key[1..].ToUpperInvariant() + " 파일" : "파일") : info.szTypeName;
        });
    }

    public string GetDisplayName(string path)
    {
        var info = default(SHFILEINFO);
        SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_DISPLAYNAME);
        return string.IsNullOrEmpty(info.szDisplayName) ? path : info.szDisplayName;
    }

    public void Dispose() => _queue.CompleteAdding();

    private static bool IsPerPath(string path, bool isDirectory)
        => isDirectory
            ? System.IO.Path.GetPathRoot(path)?.Equals(path, StringComparison.OrdinalIgnoreCase) == true
            : PerFileExtensions.Contains(System.IO.Path.GetExtension(path));

    private static string CacheKey(string path, bool isDirectory)
        => IsPerPath(path, isDirectory) ? path : isDirectory ? "<dir>" : "*" + System.IO.Path.GetExtension(path);

    private void WorkerLoop()
    {
        foreach (var (path, isDir, callback) in _queue.GetConsumingEnumerable())
        {
            var key = CacheKey(path, isDir);
            if (!_icons.TryGetValue(key, out var icon))
            {
                icon = Extract(path, isDir);
                _icons[key] = icon;
            }

            try { callback(icon); }
            catch (Exception) { /* 콜백 오류가 로더 스레드를 죽이지 않도록 */ }
        }
    }

    private static ImageSource? Extract(string path, bool isDir)
    {
        var info = default(SHFILEINFO);
        var flags = SHGFI_ICON | SHGFI_SMALLICON;
        uint attrs = 0;
        if (!IsPerPath(path, isDir))
        {
            flags |= SHGFI_USEFILEATTRIBUTES;
            attrs = isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        }

        SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (info.hIcon == IntPtr.Zero) return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }
}

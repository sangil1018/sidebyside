using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SideBySide.Shell.Abstractions;
using SideBySide.Shell.Interop;
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

    // 큰 이미지(썸네일): 경로+크기 단위 캐시, 최근 요청(지금 화면에 보이는 항목)부터 처리
    private const int MaxImageCache = 3000;
    private readonly ConcurrentDictionary<string, ImageSource?> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<(string Path, int Size, Action<ImageSource?> Callback)> _imageQueue = new(new ConcurrentStack<(string, int, Action<ImageSource?>)>());

    public ShellIconProvider()
    {
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "IconLoader", Priority = ThreadPriority.BelowNormal };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();

        // 썸네일 생성은 느릴 수 있어 아이콘과 별도 스레드 2개에서 처리
        for (int i = 0; i < 2; i++)
        {
            var thumbs = new Thread(ImageWorkerLoop) { IsBackground = true, Name = "ThumbnailLoader", Priority = ThreadPriority.BelowNormal };
            thumbs.SetApartmentState(ApartmentState.STA);
            thumbs.Start();
        }
    }

    public ImageSource? TryGetCachedImage(string path, int size)
        => _images.TryGetValue(ImageKey(path, size), out var image) ? image : null;

    public void RequestImage(string path, int size, Action<ImageSource?> callback)
    {
        if (_images.TryGetValue(ImageKey(path, size), out var image))
        {
            callback(image);
            return;
        }

        if (!_imageQueue.IsAddingCompleted) _imageQueue.Add((path, size, callback));
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

    public void Dispose()
    {
        _queue.CompleteAdding();
        _imageQueue.CompleteAdding();
    }

    private static string ImageKey(string path, int size) => size + "|" + path;

    private void ImageWorkerLoop()
    {
        foreach (var (path, size, callback) in _imageQueue.GetConsumingEnumerable())
        {
            var key = ImageKey(path, size);
            if (!_images.TryGetValue(key, out var image))
            {
                image = ExtractImage(path, size);
                if (_images.Count > MaxImageCache) _images.Clear();
                _images[key] = image;
            }

            try { callback(image); }
            catch (Exception) { /* 콜백 오류가 로더 스레드를 죽이지 않도록 */ }
        }
    }

    private static ImageSource? ExtractImage(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var ptr) != 0 || ptr == IntPtr.Zero) return null;

        var hbitmap = IntPtr.Zero;
        try
        {
            var factory = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(ptr);
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_RESIZETOFIT, out hbitmap) == 0 && hbitmap != IntPtr.Zero
                && FromHBitmap(hbitmap) is { } image)
                return image;

            // 썸네일이 비어 있으면 아이콘만 다시 요청
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            hbitmap = IntPtr.Zero;
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY, out hbitmap) == 0 && hbitmap != IntPtr.Zero)
                return FromHBitmap(hbitmap);
            return null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            Marshal.Release(ptr);
        }
    }

    /// <summary>
    /// 32bpp DIB를 알파 채널을 살려 BitmapSource로 변환
    /// (Imaging.CreateBitmapSourceFromHBitmap은 알파를 버려 배경이 검게 나옴).
    /// </summary>
    private static BitmapSource? FromHBitmap(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<DIBSECTION>(), out var dib) == 0 || dib.dsBm.bmBits == IntPtr.Zero) return null;

        var bm = dib.dsBm;
        var height = Math.Abs(bm.bmHeight);
        var stride = bm.bmWidthBytes;
        var pixels = new byte[stride * height];
        Marshal.Copy(bm.bmBits, pixels, 0, pixels.Length);

        if (dib.dsBmih.biHeight > 0)
        {
            // 아래에서 위로 저장된 DIB → 뒤집기
            var row = new byte[stride];
            for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
            {
                Buffer.BlockCopy(pixels, top * stride, row, 0, stride);
                Buffer.BlockCopy(pixels, bottom * stride, pixels, top * stride, stride);
                Buffer.BlockCopy(row, 0, pixels, bottom * stride, stride);
            }
        }

        // 내용이 전부 0인 비트맵(빈 썸네일)은 버리고 대체 아이콘을 쓰게 한다
        if (Array.TrueForAll(pixels, b => b == 0)) return null;

        PixelFormat format;
        if (bm.bmBitsPixel == 32)
        {
            var hasAlpha = false;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0)
                {
                    hasAlpha = true;
                    break;
                }
            }

            format = hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32;
        }
        else if (bm.bmBitsPixel == 24)
        {
            format = PixelFormats.Bgr24;
        }
        else
        {
            return null;
        }

        var source = BitmapSource.Create(bm.bmWidth, height, 96, 96, format, null, pixels, stride);
        source.Freeze();
        return source;
    }

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

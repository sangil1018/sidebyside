using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SideBySide.Modules.SeqViewer;

/// <summary>디코드할 프레임: 파일 + 디코드 크기 + 프리멀티플라이 해석.</summary>
public readonly record struct FrameKey(string Path, int Width, int Height, bool Premultiplied);

/// <summary>
/// 프레임 디코드 캐시.
/// <list type="bullet">
/// <item>전용 작업자 스레드(코어 수 - 1, 낮은 우선순위)가 WIC로 화면 크기에 맞춰 축소 디코드한다 (원본 해상도로 풀지 않음).</item>
/// <item>요청은 스택(최신 우선): 스크럽하면 지금 보는 프레임이 먼저 나온다. 오래된 요청은 버린다.</item>
/// <item>메모리 예산을 넘으면 가장 오래 안 쓴 프레임부터 버린다 (LRU).</item>
/// </list>
/// 결과는 UI 스레드에서 <see cref="Loaded"/>로 알린다. Dispose하면 작업자와 캐시를 모두 정리한다.
/// </summary>
public sealed class FrameCache : IDisposable
{
    private const int MaxPending = 96;

    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private readonly Dictionary<FrameKey, LinkedListNode<(FrameKey Key, BitmapSource Image, long Bytes)>> _map = [];
    private readonly LinkedList<(FrameKey Key, BitmapSource Image, long Bytes)> _lru = new();
    private readonly LinkedList<FrameKey> _pending = new();
    private readonly HashSet<FrameKey> _queued = [];
    private readonly HashSet<FrameKey> _failed = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Thread> _workers = [];
    private long _bytes;

    public FrameCache(Dispatcher dispatcher, long budgetBytes)
    {
        _dispatcher = dispatcher;
        BudgetBytes = budgetBytes;
        var count = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
        for (var i = 0; i < count; i++)
        {
            var thread = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "SeqViewer decode " + i };
            _workers.Add(thread);
            thread.Start();
        }
    }

    public long BudgetBytes { get; set; }

    public long UsedBytes => Interlocked.Read(ref _bytes);

    /// <summary>프레임이 준비됐다 (UI 스레드).</summary>
    public event Action<FrameKey>? Loaded;

    /// <summary>시스템 메모리에 맞춘 기본 예산: 사용 가능한 메모리의 1/4, 최대 1.5GB, 최소 256MB.</summary>
    public static long DefaultBudget()
    {
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return Math.Clamp(available / 4, 256L << 20, 1536L << 20);
    }

    public bool TryGet(FrameKey key, out BitmapSource image)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        image = null!;
        return false;
    }

    public bool HasFailed(FrameKey key)
    {
        lock (_gate) return _failed.Contains(key);
    }

    /// <summary>디코드 요청. <paramref name="urgent"/>면 맨 앞 (지금 볼 프레임), 아니면 뒤 (미리 읽기).</summary>
    public void Request(FrameKey key, bool urgent)
    {
        lock (_gate)
        {
            if (_map.ContainsKey(key) || _failed.Contains(key)) return;
            if (_queued.Contains(key))
            {
                if (!urgent) return;
                _pending.Remove(key); // 앞으로 옮긴다
            }
            else
            {
                _queued.Add(key);
            }

            if (urgent) _pending.AddFirst(key);
            else _pending.AddLast(key);

            // 너무 쌓이면 가장 덜 급한(뒤쪽) 요청을 버린다
            while (_pending.Count > MaxPending)
            {
                _queued.Remove(_pending.Last!.Value);
                _pending.RemoveLast();
            }
        }

        _signal.Release();
    }

    /// <summary>대기 중인 미리 읽기를 모두 버린다 (크기가 바뀌었을 때).</summary>
    public void ClearPending()
    {
        lock (_gate)
        {
            _pending.Clear();
            _queued.Clear();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            _queued.Clear();
            _map.Clear();
            _lru.Clear();
            _failed.Clear();
            Interlocked.Exchange(ref _bytes, 0);
        }
    }

    /// <summary>파일 머리글만 읽어 원본 크기를 얻는다.</summary>
    public static (int Width, int Height) ProbeSize(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        return (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
    }

    /// <summary>
    /// 지정 크기 Pbgra32로 디코드. 프리멀티플라이 파일(합성 프로그램 출력 등)은 픽셀을 다시 곱하지 않고 그대로 미리 곱한 값으로 해석한다.
    /// </summary>
    internal static BitmapSource Decode(FrameKey key)
    {
        BitmapSource image;
        using (var stream = new FileStream(key.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            // IgnoreImageCache는 StreamSource와 함께 쓰면 WPF가 ArgumentNullException을 던진다 (스트림은 원래 캐시되지 않음)
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.StreamSource = stream;
            bitmap.DecodePixelWidth = key.Width;
            bitmap.DecodePixelHeight = key.Height;
            bitmap.EndInit();
            image = bitmap;
        }

        if (image.PixelWidth != key.Width || image.PixelHeight != key.Height)
            image = new TransformedBitmap(image, new ScaleTransform(key.Width / (double)image.PixelWidth, key.Height / (double)image.PixelHeight));

        BitmapSource result;
        if (key.Premultiplied)
        {
            // 직선 알파로 읽은 바이트를 그대로 미리 곱한 형식으로 (값이 알파를 넘지 않게만 자른다)
            var straight = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var stride = key.Width * 4;
            var pixels = new byte[stride * key.Height];
            straight.CopyPixels(pixels, stride, 0);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var a = pixels[i + 3];
                if (pixels[i] > a) pixels[i] = a;
                if (pixels[i + 1] > a) pixels[i + 1] = a;
                if (pixels[i + 2] > a) pixels[i + 2] = a;
            }

            result = BitmapSource.Create(key.Width, key.Height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        }
        else
        {
            result = image.Format == PixelFormats.Pbgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
            if (result is not WriteableBitmap and not CachedBitmap)
            {
                // 변환 체인을 한 번 펼쳐 둔다 (그릴 때마다 다시 변환하지 않게)
                result = new CachedBitmap(result, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
        }

        result.Freeze();
        return result;
    }

    private void Work()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                _signal.Wait(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            FrameKey key;
            lock (_gate)
            {
                if (_pending.First is not { } first) continue;
                key = first.Value;
                _pending.RemoveFirst();
            }

            BitmapSource? image = null;
            try
            {
                image = Decode(key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException
                                           or ArgumentException or InvalidOperationException or OverflowException
                                           or System.Runtime.InteropServices.COMException)
            {
                lock (_gate) _failed.Add(key);
            }

            lock (_gate)
            {
                _queued.Remove(key);
                if (image is not null && !_map.ContainsKey(key))
                {
                    var bytes = (long)image.PixelWidth * image.PixelHeight * 4;
                    _map[key] = _lru.AddFirst((key, image, bytes));
                    _bytes += bytes;
                    while (_bytes > BudgetBytes && _lru.Count > 1)
                    {
                        var last = _lru.Last!.Value;
                        _lru.RemoveLast();
                        _map.Remove(last.Key);
                        _bytes -= last.Bytes;
                    }
                }
            }

            if (token.IsCancellationRequested) return;
            _dispatcher.BeginInvoke(DispatcherPriority.Render, () => Loaded?.Invoke(key));
        }
    }

    public void Dispose()
    {
        // 작업자가 대기 중일 수 있어 신호·토큰 개체는 해제하지 않고 취소만 한다 (작업자는 곧 스스로 끝난다)
        _cts.Cancel();
        Loaded = null;
        Clear();
    }
}

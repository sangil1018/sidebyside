using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.SeqViewer;

/// <summary>
/// 뷰어 화면과 그리기 엔진.
/// <list type="bullet">
/// <item>모든 레이어가 Normal이면 레이어별 Image를 쌓아 WPF(GPU)가 합성한다.</item>
/// <item>특수 블렌드가 하나라도 있으면 <see cref="Compositor"/>가 작업자에서 여러 코어로 합성한 한 장을 보인다.</item>
/// <item>디코드 크기는 확대 배율·DPI에 맞춰 1/8 단계로 정한다 (필요 이상 큰 이미지를 풀지 않음).</item>
/// <item>재생은 다음 프레임이 디코드된 뒤에만 넘어간다 (끊김 대신 대기), 앞쪽 16프레임을 미리 읽는다.</item>
/// </list>
/// </summary>
public partial class ViewerView : UserControl
{
    public static readonly int MinusOne = -1;
    public static readonly int PlusOne = 1;
    private const int PrefetchFrames = 16;

    private readonly ViewerViewModel _vm;
    private readonly FrameCache _cache;
    private readonly HashSet<FrameKey> _waiting = [];
    private readonly Dictionary<string, (int W, int H)> _sizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Image _composite = new() { Stretch = Stretch.Fill };
    private int _nativeW;
    private int _nativeH;
    private int _decodeW;
    private int _decodeH;
    private double _zoom = 1;
    private bool _fit = true;
    private int _generation;
    private bool _playingHooked;
    private TimeSpan _lastTick;
    private double _elapsed;
    private Point? _dragStart;
    private Point _panStart;

    public ViewerView(ViewerViewModel vm, FrameCache cache)
    {
        _vm = vm;
        _cache = cache;
        InitializeComponent();
        DataContext = vm;

        vm.RenderRequested += (_, _) => Render();
        vm.PropertyChanged += OnViewModelChanged;
        cache.Loaded += OnFrameLoaded;
        PreviewKeyDown += OnKey;
        Loaded += (_, _) => Focus();
        Unloaded += (_, _) => SetPlaying(false);
        Layout.SizeChanged += (_, _) => UpdateLayoutMode();
    }

    // ── 그리기 ──────────────────────────────────────────────────

    /// <summary>현재 프레임의 보이는 레이어 (아래부터) 와 각 키.</summary>
    private List<(LayerViewModel Layer, FrameKey Key)> VisibleAt(int frame)
    {
        var list = new List<(LayerViewModel, FrameKey)>();
        if (_decodeW == 0) return list;
        for (var i = _vm.Layers.Count - 1; i >= 0; i--)
        {
            var layer = _vm.Layers[i];
            if (!layer.Visible || layer.Opacity <= 0) continue;
            var index = layer.FileIndexAt(frame);
            if (index < 0) continue;
            list.Add((layer, new FrameKey(layer.Source.Files[index], _decodeW, _decodeH, layer.Premultiplied)));
        }

        return list;
    }

    private void Render()
    {
        if (_vm.Layers.Count == 0)
        {
            Stage.Children.Clear();
            EmptyText.Visibility = Visibility.Visible;
            _nativeW = _nativeH = _decodeW = _decodeH = 0;
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;
        if (!EnsureCanvasSize()) return;

        var frame = _vm.CurrentFrame;
        var layers = VisibleAt(frame);
        _waiting.Clear();
        foreach (var (_, key) in layers)
        {
            if (_cache.TryGet(key, out _) || _cache.HasFailed(key)) continue;
            _waiting.Add(key);
        }

        Prefetch(frame);
        foreach (var key in _waiting) _cache.Request(key, urgent: true); // 마지막에 넣은 것이 가장 앞
        if (_waiting.Count > 0) return; // 준비될 때까지 이전 화면 유지 (깜박임 없음)

        if (layers.All(l => l.Layer.Blend == BlendMode.Normal)) DrawStacked(layers);
        else DrawComposited(layers);
        UpdateCacheText();
    }

    private void DrawStacked(List<(LayerViewModel Layer, FrameKey Key)> layers)
    {
        _generation++;
        if (Stage.Children.Contains(_composite)) Stage.Children.Remove(_composite);
        var images = Stage.Children.OfType<Image>().ToList();
        while (images.Count < layers.Count)
        {
            var image = new Image { Stretch = Stretch.Fill };
            Stage.Children.Add(image);
            images.Add(image);
        }

        for (var i = 0; i < images.Count; i++)
        {
            if (i >= layers.Count)
            {
                Stage.Children.Remove(images[i]);
                continue;
            }

            images[i].Source = _cache.TryGet(layers[i].Key, out var bitmap) ? bitmap : null;
            images[i].Opacity = layers[i].Layer.Opacity;
        }
    }

    private void DrawComposited(List<(LayerViewModel Layer, FrameKey Key)> layers)
    {
        var generation = ++_generation;
        var inputs = layers
            .Select(l => (Bitmap: _cache.TryGet(l.Key, out var b) ? b : null, l.Layer.Blend, l.Layer.Opacity))
            .Where(l => l.Bitmap is not null)
            .ToList();
        int w = _decodeW, h = _decodeH;

        Task.Run(() =>
        {
            var parts = inputs.Select(i =>
            {
                var pixels = new byte[w * h * 4];
                i.Bitmap!.CopyPixels(pixels, w * 4, 0);
                return new CompositeLayer(pixels, i.Blend, i.Opacity);
            }).ToList();
            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, Compositor.Compose(parts, w, h), w * 4);
            result.Freeze();
            return result;
        }).ContinueWith(t =>
        {
            if (t.IsFaulted || generation != _generation) return; // 그 사이 다른 프레임을 그렸다
            foreach (var image in Stage.Children.OfType<Image>().Where(i => !ReferenceEquals(i, _composite)).ToList()) Stage.Children.Remove(image);
            if (!Stage.Children.Contains(_composite)) Stage.Children.Add(_composite);
            _composite.Source = t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Prefetch(int frame)
    {
        if (_vm.TotalFrames <= 1) return;
        for (var i = 1; i <= PrefetchFrames; i++)
        {
            foreach (var (_, key) in VisibleAt((frame + i) % _vm.TotalFrames)) _cache.Request(key, urgent: false);
        }
    }

    private void OnFrameLoaded(FrameKey key)
    {
        if (_waiting.Remove(key) && _waiting.Count == 0) Render();
        UpdateCacheText();
    }

    /// <summary>캔버스(원본) 크기와 디코드 크기를 정한다. 맨 아래 보이는 레이어의 첫 파일 기준 (원본 도구와 같음).</summary>
    private bool EnsureCanvasSize()
    {
        var bottom = _vm.Layers.LastOrDefault(l => l.Visible) ?? _vm.Layers[^1];
        var file = bottom.Source.Files[0];
        if (!_sizes.TryGetValue(file, out var size))
        {
            try
            {
                size = FrameCache.ProbeSize(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException
                                           or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                _vm.Status = L.F("'{0}'을(를) 읽을 수 없습니다: {1}", Path.GetFileName(file), ex.Message);
                return false;
            }

            _sizes[file] = size;
        }

        if (size.W != _nativeW || size.H != _nativeH)
        {
            _nativeW = size.W;
            _nativeH = size.H;
            Stage.Width = _nativeW;
            Stage.Height = _nativeH;
            _fit = true;
        }

        if (_fit) FitToView();
        UpdateDecodeSize();
        return _decodeW > 0;
    }

    /// <summary>확대 배율 × DPI에 필요한 해상도만 디코드 (1/8 단계, 원본 이하).</summary>
    private void UpdateDecodeSize()
    {
        if (_nativeW == 0) return;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var scale = Math.Min(1, Math.Ceiling(_zoom * dpi * 8) / 8);
        var w = Math.Max(1, (int)Math.Round(_nativeW * scale));
        var h = Math.Max(1, (int)Math.Round(_nativeH * scale));
        if (w == _decodeW && h == _decodeH) return;
        _decodeW = w;
        _decodeH = h;
        _cache.ClearPending();
    }

    private void UpdateCacheText()
        => CacheText.Text = L.F("캐시 {0:N0} / {1:N0} MB", _cache.UsedBytes >> 20, _cache.BudgetBytes >> 20);

    // ── 확대·이동 ───────────────────────────────────────────────

    private void FitToView()
    {
        if (_nativeW == 0 || Viewport.ActualWidth <= 0) return;
        _zoom = Math.Min(Viewport.ActualWidth / _nativeW, Viewport.ActualHeight / _nativeH);
        StagePan.X = (Viewport.ActualWidth - (_nativeW * _zoom)) / 2;
        StagePan.Y = (Viewport.ActualHeight - (_nativeH * _zoom)) / 2;
        ApplyZoom();
    }

    private void SetZoom(double zoom, Point around)
    {
        zoom = Math.Clamp(zoom, 0.02, 32);
        var ratio = zoom / _zoom;
        StagePan.X = around.X - ((around.X - StagePan.X) * ratio);
        StagePan.Y = around.Y - ((around.Y - StagePan.Y) * ratio);
        _zoom = zoom;
        _fit = false;
        ApplyZoom();
        var before = (_decodeW, _decodeH);
        UpdateDecodeSize();
        if (before != (_decodeW, _decodeH)) Render();
    }

    private void ApplyZoom()
    {
        StageScale.ScaleX = StageScale.ScaleY = _zoom;
        ZoomText.Text = $"{_zoom * 100:0}%";
    }

    private void OnFit(object sender, RoutedEventArgs e)
    {
        _fit = true;
        FitToView();
        Render();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        SetZoom(_zoom * Math.Pow(1.15, e.Delta / 120.0), e.GetPosition(Viewport));
        e.Handled = true;
    }

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Middle)) return;
        if (e.ClickCount == 2)
        {
            OnFit(sender, e);
            return;
        }

        _dragStart = e.GetPosition(Viewport);
        _panStart = new Point(StagePan.X, StagePan.Y);
        Viewport.CaptureMouse();
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start) return;
        var p = e.GetPosition(Viewport);
        StagePan.X = _panStart.X + (p.X - start.X);
        StagePan.Y = _panStart.Y + (p.Y - start.Y);
        _fit = false;
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        Viewport.ReleaseMouseCapture();
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_fit) return;
        FitToView();
        var before = (_decodeW, _decodeH);
        UpdateDecodeSize();
        if (before != (_decodeW, _decodeH)) Render();
    }

    /// <summary>좁으면(오른쪽 패널) 레이어 목록을 화면 아래로.</summary>
    private void UpdateLayoutMode()
    {
        var narrow = Layout.ActualWidth < 700;
        Grid.SetRow(SidePanel, narrow ? 1 : 0);
        Grid.SetColumn(SidePanel, narrow ? 0 : 1);
        Grid.SetColumnSpan(SidePanel, narrow ? 2 : 1);
        SidePanel.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(12, 0, 0, 0);
        SideColumn.Width = narrow ? new GridLength(0) : new GridLength(290);
        SideRow.Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    // ── 재생 ────────────────────────────────────────────────────

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewerViewModel.IsPlaying))
        {
            SetPlaying(_vm.IsPlaying);
            PlayButton.Content = _vm.IsPlaying ? Glyphs.Pause : Glyphs.Play;
        }
    }

    private void SetPlaying(bool playing)
    {
        if (playing == _playingHooked) return;
        _playingHooked = playing;
        _elapsed = 0;
        _lastTick = TimeSpan.Zero;
        if (playing) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs r) return;
        if (_lastTick == TimeSpan.Zero)
        {
            _lastTick = r.RenderingTime;
            return;
        }

        _elapsed += (r.RenderingTime - _lastTick).TotalSeconds;
        _lastTick = r.RenderingTime;
        var interval = 1.0 / Math.Clamp(_vm.Fps, 1, 120);
        if (_elapsed < interval) return;

        // 다음 프레임이 준비됐을 때만 넘긴다. 뒤처져도 몰아서 건너뛰지 않는다.
        var next = _vm.CurrentFrame >= _vm.LastFrame ? 0 : _vm.CurrentFrame + 1;
        var ready = VisibleAt(next).All(l => _cache.TryGet(l.Key, out _) || _cache.HasFailed(l.Key));
        if (!ready)
        {
            foreach (var (_, key) in VisibleAt(next)) _cache.Request(key, urgent: true);
            return;
        }

        _elapsed = Math.Min(_elapsed - interval, interval);
        _vm.Advance();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox or ComboBox or ComboBoxItem) return;
        switch (e.Key)
        {
            case Key.Space:
                _vm.TogglePlayCommand.Execute(null);
                break;
            case Key.Left:
                _vm.StepCommand.Execute(-1);
                break;
            case Key.Right:
                _vm.StepCommand.Execute(1);
                break;
            case Key.Home:
                _vm.GoToStartCommand.Execute(null);
                break;
            case Key.End:
                _vm.GoToEndCommand.Execute(null);
                break;
            case Key.F or Key.D0 or Key.NumPad0:
                OnFit(this, e);
                break;
            case Key.D1 or Key.NumPad1:
                SetZoom(0.5, Center());
                break;
            case Key.D2 or Key.NumPad2:
                SetZoom(1, Center());
                break;
            case Key.D3 or Key.NumPad3:
                SetZoom(2, Center());
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private Point Center() => new(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2);

    /// <summary>화면을 닫을 때: 재생 중지, 표시 이미지 해제.</summary>
    public void Release()
    {
        SetPlaying(false);
        _generation++;
        Stage.Children.Clear();
        _composite.Source = null;
        _waiting.Clear();
        _cache.Loaded -= OnFrameLoaded;
    }
}

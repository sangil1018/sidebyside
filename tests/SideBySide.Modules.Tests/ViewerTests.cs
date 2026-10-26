using SideBySide.Modules.SeqViewer;

namespace SideBySide.Modules.Tests;

public class ViewerTests
{
    private static byte[] Pixel(byte b, byte g, byte r, byte a) => [b, g, r, a];

    private static byte[] Compose(BlendMode mode, byte[] bottom, byte[] top, double opacity = 1)
        => Compositor.Compose([new CompositeLayer(bottom, BlendMode.Normal, 1), new CompositeLayer(top, mode, opacity)], 1, 1);

    [Fact]
    public void Normal_is_source_over()
    {
        Assert.Equal(Pixel(0, 0, 255, 255), Compose(BlendMode.Normal, Pixel(255, 0, 0, 255), Pixel(0, 0, 255, 255)));

        // 50% 불투명도: 반씩 섞임
        var half = Compose(BlendMode.Normal, Pixel(255, 0, 0, 255), Pixel(0, 0, 255, 255), 0.5);
        Assert.InRange(half[0], 126, 129);
        Assert.InRange(half[2], 126, 129);
        Assert.Equal(255, half[3]);
    }

    [Fact]
    public void Multiply_screen_add_and_difference()
    {
        var gray = Pixel(128, 128, 128, 255);
        Assert.InRange(Compose(BlendMode.Multiply, gray, Pixel(255, 255, 255, 255))[0], 127, 129);
        Assert.Equal(0, Compose(BlendMode.Multiply, gray, Pixel(0, 0, 0, 255))[0]);
        Assert.Equal(255, Compose(BlendMode.Screen, gray, Pixel(255, 255, 255, 255))[0]);
        Assert.Equal(255, Compose(BlendMode.Add, Pixel(200, 200, 200, 255), Pixel(100, 100, 100, 255))[0]);
        Assert.Equal(0, Compose(BlendMode.Difference, gray, gray)[0]);
    }

    [Fact]
    public void Alpha_modes_cut_the_layer_below()
    {
        var red = Pixel(0, 0, 255, 255);
        Assert.Equal(0, Compose(BlendMode.AlphaIn, red, Pixel(0, 0, 0, 0))[3]);
        Assert.Equal(0, Compose(BlendMode.AlphaOut, red, Pixel(0, 0, 0, 255))[3]);
        Assert.Equal(red, Compose(BlendMode.Behind, red, Pixel(255, 0, 0, 255)));
    }

    [Fact]
    public void Transparent_top_keeps_bottom_for_every_mode()
    {
        var bottom = Pixel(10, 20, 30, 255);
        foreach (var mode in Enum.GetValues<BlendMode>().Where(m => m != BlendMode.AlphaIn))
            Assert.Equal(bottom, Compose(mode, bottom, Pixel(0, 0, 0, 0)));
    }

    [Fact]
    public void Premultiplied_output_never_exceeds_alpha()
    {
        var rnd = new Random(1);
        foreach (var mode in Enum.GetValues<BlendMode>())
        {
            for (var i = 0; i < 50; i++)
            {
                byte a1 = (byte)rnd.Next(256), a2 = (byte)rnd.Next(256);
                var bottom = Pixel((byte)rnd.Next(a1 + 1), (byte)rnd.Next(a1 + 1), (byte)rnd.Next(a1 + 1), a1);
                var top = Pixel((byte)rnd.Next(a2 + 1), (byte)rnd.Next(a2 + 1), (byte)rnd.Next(a2 + 1), a2);
                var o = Compose(mode, bottom, top, rnd.NextDouble());
                Assert.True(o[0] <= o[3] && o[1] <= o[3] && o[2] <= o[3], $"{mode}");
            }
        }
    }

    [Fact]
    public void Layer_frame_mapping_matches_seq_viewer()
    {
        var source = new LayerSource { Name = "s", Directory = "", Files = ["a", "b", "c"], Numbers = [1, 2, 3] };
        var layer = new LayerViewModel(source) { Offset = 2 };
        Assert.Equal(-1, layer.FileIndexAt(1));
        Assert.Equal(0, layer.FileIndexAt(2));
        Assert.Equal(2, layer.FileIndexAt(4));
        Assert.Equal(-1, layer.FileIndexAt(5));

        layer.HoldFrame = true;
        Assert.Equal(2, layer.FileIndexAt(50));
        Assert.Equal(-1, layer.FileIndexAt(0));
    }

    [Fact]
    public void Single_images_become_held_layers()
    {
        using var d = new TempDir();
        d.File("bg.png", "x");
        d.File("fx_0001.png", "x");
        d.File("fx_0002.png", "x");
        var layers = LayerSource.Scan(d.Path, 0, CancellationToken.None);
        Assert.Equal(2, layers.Count);
        var bg = layers.Single(l => l.Name == "bg.png");
        Assert.True(bg.IsSingleImage);
        Assert.True(new LayerViewModel(bg).HoldFrame);
        Assert.Equal("fx_####.png", layers.Single(l => !l.IsSingleImage).Name);
    }

    [Fact]
    public void Decodes_at_requested_size_and_premultiplied_mode_keeps_bytes()
    {
        using var d = new TempDir();
        var path = ResizeTests.MakeImage(Path.Combine(d.Path, "f.png"), 64, 32, new System.Windows.Media.Imaging.PngBitmapEncoder());
        var image = FrameCache.Decode(new FrameKey(path, 16, 8, false));
        Assert.Equal(16, image.PixelWidth);
        Assert.Equal(8, image.PixelHeight);
        Assert.Equal(System.Windows.Media.PixelFormats.Pbgra32, image.Format);
        Assert.True(image.IsFrozen);
        Assert.Equal((64, 32), FrameCache.ProbeSize(path));

        var premul = FrameCache.Decode(new FrameKey(path, 64, 32, true));
        var pixels = new byte[64 * 32 * 4];
        premul.CopyPixels(pixels, 64 * 4, 0);
        Assert.Equal(200, pixels[0]); // 다시 곱하지 않음
    }

    [Fact]
    public async Task Cache_loads_frames_on_worker_threads_and_respects_budget()
    {
        using var d = new TempDir();
        var files = Enumerable.Range(1, 6).Select(i => ResizeTests.MakeImage(Path.Combine(d.Path, $"f_{i:0000}.png"), 64, 64, new System.Windows.Media.Imaging.PngBitmapEncoder())).ToList();
        var dispatcherThread = new Thread(System.Windows.Threading.Dispatcher.Run) { IsBackground = true };
        dispatcherThread.SetApartmentState(ApartmentState.STA);
        dispatcherThread.Start();
        System.Windows.Threading.Dispatcher? dispatcher = null;
        while ((dispatcher = System.Windows.Threading.Dispatcher.FromThread(dispatcherThread)) is null) await Task.Delay(10);

        using var cache = new FrameCache(dispatcher, budgetBytes: 64 * 64 * 4 * 3);
        var loaded = 0;
        var all = new TaskCompletionSource();
        cache.Loaded += _ =>
        {
            if (Interlocked.Increment(ref loaded) == files.Count) all.TrySetResult();
        };
        foreach (var f in files) cache.Request(new FrameKey(f, 64, 64, false), urgent: false);
        await all.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(cache.UsedBytes <= cache.BudgetBytes);
        var cached = files.Count(f => cache.TryGet(new FrameKey(f, 64, 64, false), out _));
        Assert.InRange(cached, 1, 3); // 예산(3장)을 넘는 것은 오래된 순으로 버려졌다
        Assert.DoesNotContain(files, f => cache.HasFailed(new FrameKey(f, 64, 64, false)));
        dispatcher.InvokeShutdown();
    }
}

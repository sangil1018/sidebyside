using System.Windows.Media;
using System.Windows.Media.Imaging;
using SideBySide.Modules;
using SideBySide.Modules.Resize;

namespace SideBySide.Modules.Tests;

public class ResizeTests
{
    internal static string MakeImage(string path, int width, int height, BitmapEncoder encoder)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200;
            pixels[i + 1] = 100;
            pixels[i + 2] = 50;
            pixels[i + 3] = 255;
        }

        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static (int W, int H) SizeOf(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    [Theory]
    [InlineData(ResizeKind.Percent, 50, 0, 0, true, false, 2000, 1000, 1000, 500)]
    [InlineData(ResizeKind.Box, 0, 1920, 1080, true, false, 4000, 1000, 1920, 480)]
    [InlineData(ResizeKind.Box, 0, 1920, 1080, false, false, 4000, 1000, 1920, 1080)]
    [InlineData(ResizeKind.Box, 0, 1920, 1080, true, true, 800, 600, 800, 600)]
    [InlineData(ResizeKind.Percent, 200, 0, 0, true, true, 100, 50, 100, 50)]
    [InlineData(ResizeKind.Percent, 200, 0, 0, true, false, 100, 50, 200, 100)]
    [InlineData(ResizeKind.Box, 0, 960, 540, false, true, 800, 1000, 800, 540)]
    public void Target_size(ResizeKind kind, double percent, int w, int h, bool keep, bool noUp, int srcW, int srcH, int expW, int expH)
    {
        var options = new ResizeOptions { Kind = kind, Percent = percent, Width = w, Height = h, KeepAspect = keep, NoUpscale = noUp };
        Assert.Equal((expW, expH), ImageResizer.TargetSize(srcW, srcH, options));
    }

    [Fact]
    public async Task Resizes_with_suffix_in_parallel()
    {
        using var d = new TempDir();
        var a = MakeImage(Path.Combine(d.Path, "a.png"), 400, 200, new PngBitmapEncoder());
        var b = MakeImage(Path.Combine(d.Path, "b.jpg"), 300, 300, new JpegBitmapEncoder());
        d.File("notes.txt", "x");
        var options = new ResizeOptions { Kind = ResizeKind.Percent, Percent = 50, Output = ResizeOutput.Suffix, Suffix = "_half" };

        var (jobs, errors) = ImageResizer.Plan([d.Path], options, ImageFormats.IsImage);
        Assert.Empty(errors);
        Assert.Equal(2, jobs.Count);

        var summary = await ImageResizer.RunAsync(jobs, options, null, CancellationToken.None);
        Assert.Equal(2, summary.Succeeded);
        Assert.Empty(summary.Errors);
        Assert.Equal((200, 100), SizeOf(Path.Combine(d.Path, "a_half.png")));
        Assert.Equal((150, 150), SizeOf(Path.Combine(d.Path, "b_half.jpg")));
        Assert.Equal((400, 200), SizeOf(a)); // 원본은 그대로

        // 다시 실행해도 이미 접미사가 붙은 결과는 다시 처리하지 않는다
        var (again, _) = ImageResizer.Plan([d.Path], options, ImageFormats.IsImage);
        Assert.Equal(2, again.Count);
        _ = b;
    }

    [Fact]
    public async Task Overwrite_replaces_in_place_without_temp_files()
    {
        using var d = new TempDir();
        var a = MakeImage(Path.Combine(d.Path, "a.png"), 100, 100, new PngBitmapEncoder());
        var options = new ResizeOptions { Kind = ResizeKind.Box, Width = 40, Height = 20, KeepAspect = false, Output = ResizeOutput.Overwrite };
        var (jobs, _) = ImageResizer.Plan([a], options, ImageFormats.IsImage);

        await ImageResizer.RunAsync(jobs, options, null, CancellationToken.None);

        Assert.Equal((40, 20), SizeOf(a));
        Assert.Single(Directory.GetFiles(d.Path));
    }

    [Fact]
    public async Task Folder_output_mirrors_subfolders_and_converts_unwritable_formats()
    {
        using var d = new TempDir();
        var src = d.Dir("src");
        MakeImage(Path.Combine(src, "sub", "deep.png"), 64, 64, new PngBitmapEncoder());
        MakeImage(Path.Combine(src, "photo.jxr"), 64, 32, new WmpBitmapEncoder());
        var output = Path.Combine(d.Path, "out");
        var options = new ResizeOptions { Kind = ResizeKind.Percent, Percent = 25, Output = ResizeOutput.Folder, OutputFolder = output, Suffix = string.Empty, IncludeSubfolders = true };

        var (jobs, errors) = ImageResizer.Plan([src], options, ImageFormats.IsImage);
        Assert.Empty(errors);
        await ImageResizer.RunAsync(jobs, options, null, CancellationToken.None);

        Assert.Equal((16, 16), SizeOf(Path.Combine(output, "sub", "deep.png")));
        Assert.True(File.Exists(Path.Combine(output, "photo.png")));
        Assert.Contains(jobs, j => j.ConvertedToPng);
    }

    [Fact]
    public void Plan_rejects_output_equal_to_source()
    {
        using var d = new TempDir();
        var a = MakeImage(Path.Combine(d.Path, "a.png"), 10, 10, new PngBitmapEncoder());
        var options = new ResizeOptions { Output = ResizeOutput.Folder, OutputFolder = d.Path, Suffix = string.Empty };
        var (jobs, errors) = ImageResizer.Plan([a], options, ImageFormats.IsImage);
        Assert.Empty(jobs);
        Assert.Single(errors);
    }

    [Fact]
    public void Plan_requires_suffix_and_valid_characters()
    {
        Assert.NotEmpty(ImageResizer.Plan([], new ResizeOptions { Output = ResizeOutput.Suffix, Suffix = string.Empty }, ImageFormats.IsImage).Errors);
        Assert.NotEmpty(ImageResizer.Plan([], new ResizeOptions { Output = ResizeOutput.Suffix, Suffix = "a|b" }, ImageFormats.IsImage).Errors);
        Assert.NotEmpty(ImageResizer.Plan([], new ResizeOptions { Output = ResizeOutput.Folder, OutputFolder = null }, ImageFormats.IsImage).Errors);
    }

    [Fact]
    public async Task Broken_image_is_reported_not_thrown()
    {
        using var d = new TempDir();
        var bad = d.File("bad.png", "not an image");
        var options = new ResizeOptions { Output = ResizeOutput.Suffix };
        var (jobs, _) = ImageResizer.Plan([bad], options, ImageFormats.IsImage);
        var summary = await ImageResizer.RunAsync(jobs, options, null, CancellationToken.None);
        Assert.Equal(0, summary.Succeeded);
        Assert.Single(summary.Errors);
    }
}

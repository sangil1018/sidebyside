using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.Resize;

/// <summary>크기 지정 방식.</summary>
public enum ResizeKind
{
    /// <summary>원본의 n%.</summary>
    Percent,

    /// <summary>지정한 가로×세로 (비율 유지를 켜면 그 상자 안에 맞춤).</summary>
    Box,
}

/// <summary>결과를 쓰는 곳.</summary>
public enum ResizeOutput
{
    /// <summary>원본을 바꾼다 (임시 파일에 쓴 뒤 교체).</summary>
    Overwrite,

    /// <summary>같은 폴더에 접미사를 붙여 새 파일로.</summary>
    Suffix,

    /// <summary>다른 폴더에 (하위 폴더 구조 유지, 접미사 선택).</summary>
    Folder,
}

public sealed record ResizeOptions
{
    public ResizeKind Kind { get; init; } = ResizeKind.Percent;

    public double Percent { get; init; } = 50;

    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    public bool KeepAspect { get; init; } = true;

    /// <summary>원본보다 크게 만들지 않는다.</summary>
    public bool NoUpscale { get; init; } = true;

    public ResizeOutput Output { get; init; } = ResizeOutput.Suffix;

    public string Suffix { get; init; } = "_resized";

    public string? OutputFolder { get; init; }

    public bool IncludeSubfolders { get; init; }

    /// <summary>JPEG 품질 (1..100).</summary>
    public int JpegQuality { get; init; } = 90;
}

/// <summary>파일 하나의 작업: 원본 → 결과 경로. <see cref="ConvertedToPng"/>는 원본 형식으로 저장할 수 없어 PNG로 바꾼 경우.</summary>
public sealed record ResizeJob(string Source, string Destination, bool ConvertedToPng);

public sealed record ResizeSummary(int Succeeded, int Skipped, IReadOnlyList<string> Errors, IReadOnlyCollection<string> TouchedFolders);

/// <summary>
/// file-resizer 이식. Windows 이미지 코덱(WIC)으로 디코더 단계에서 바로 축소 디코드하고(JPEG는 DCT 축소까지 사용),
/// 파일마다 독립이라 CPU 코어 수만큼 병렬 처리한다. 디코드·인코드가 병목이라 GPU로 옮겨도 이득이 없다.
/// </summary>
public static class ImageResizer
{
    /// <summary>압축 폭탄 방지: 이보다 픽셀이 많은 이미지는 건너뛴다 (2억 픽셀 ≈ 800MB 메모리).</summary>
    public const long MaxPixels = 200_000_000;

    private static readonly HashSet<string> WritableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".bmp", ".gif", ".tif", ".tiff",
    };

    /// <summary>원본 크기에서 결과 크기. 0 이하가 되지 않는다.</summary>
    public static (int Width, int Height) TargetSize(int width, int height, ResizeOptions options)
    {
        double w, h;
        if (options.Kind == ResizeKind.Percent)
        {
            var scale = Math.Clamp(options.Percent, 1, 1000) / 100.0;
            w = width * scale;
            h = height * scale;
        }
        else if (options.KeepAspect)
        {
            var scale = Math.Min(options.Width / (double)width, options.Height / (double)height);
            w = width * scale;
            h = height * scale;
        }
        else
        {
            w = options.Width;
            h = options.Height;
        }

        if (options.NoUpscale && (w > width || h > height))
        {
            // 비율을 유지하는 경우는 원본 크기로, 늘이는 경우는 각 축을 원본 이하로
            if (options.Kind == ResizeKind.Percent || options.KeepAspect) return (width, height);
            w = Math.Min(w, width);
            h = Math.Min(h, height);
        }

        return (Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
    }

    /// <summary>
    /// 입력(파일·폴더)을 작업 목록으로. 폴더는 이미지 파일만, 하위 폴더는 옵션에 따라.
    /// 접미사 모드에서는 이미 접미사가 붙은 파일을 건너뛰어 다시 실행해도 결과가 쌓이지 않게 한다.
    /// </summary>
    public static (List<ResizeJob> Jobs, List<string> Errors) Plan(IReadOnlyList<string> inputs, ResizeOptions options, Func<string, bool> isImage)
    {
        var jobs = new List<ResizeJob>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (options.Output == ResizeOutput.Folder && string.IsNullOrWhiteSpace(options.OutputFolder))
        {
            errors.Add(L.T("저장할 폴더를 고르세요."));
            return (jobs, errors);
        }

        if (options.Output == ResizeOutput.Suffix && string.IsNullOrEmpty(options.Suffix))
        {
            errors.Add(L.T("접미사를 입력하세요. 원본을 바꾸려면 '원본 덮어쓰기'를 고르세요."));
            return (jobs, errors);
        }

        if (options.Suffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            errors.Add(L.T("접미사에 파일 이름으로 쓸 수 없는 문자가 있습니다."));
            return (jobs, errors);
        }

        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                var enumeration = new EnumerationOptions
                {
                    RecurseSubdirectories = options.IncludeSubfolders,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden,
                };
                var outputRoot = options.OutputFolder is { } folder ? Path.GetFullPath(folder) : null;
                foreach (var file in Directory.EnumerateFiles(input, "*", enumeration))
                {
                    if (!isImage(file)) continue;

                    // 결과 폴더가 입력 폴더 안에 있으면 이전 결과를 다시 처리하지 않게
                    if (outputRoot is not null && IsUnder(file, outputRoot)) continue;
                    Add(file, Path.GetRelativePath(input, file));
                }
            }
            else if (File.Exists(input) && isImage(input))
            {
                Add(input, Path.GetFileName(input));
            }
        }

        return (jobs, errors);

        void Add(string source, string relative)
        {
            if (!seen.Add(source)) return;
            var name = Path.GetFileNameWithoutExtension(source);
            if (options.Output == ResizeOutput.Suffix && name.EndsWith(options.Suffix, StringComparison.OrdinalIgnoreCase)) return;

            var ext = Path.GetExtension(source);
            var png = !WritableExtensions.Contains(ext);
            var outExt = png ? ".png" : ext;
            var dest = options.Output switch
            {
                ResizeOutput.Overwrite => png ? Path.ChangeExtension(source, ".png") : source,
                ResizeOutput.Suffix => Path.Combine(Path.GetDirectoryName(source)!, name + options.Suffix + outExt),
                _ => Path.Combine(options.OutputFolder!, Path.GetDirectoryName(relative) ?? string.Empty, name + options.Suffix + outExt),
            };
            dest = Path.GetFullPath(dest);

            if (options.Output != ResizeOutput.Overwrite && string.Equals(dest, Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(L.F("'{0}': 결과가 원본과 같은 파일이 됩니다. 접미사나 다른 폴더를 지정하세요.", Path.GetFileName(source)));
                return;
            }

            // PNG로 바꿔 저장할 때 같은 이름의 다른 파일을 덮어쓰지 않게
            if (png && options.Output == ResizeOutput.Overwrite && File.Exists(dest))
            {
                errors.Add(L.F("'{0}': PNG로 저장해야 하는데 '{1}'이(가) 이미 있습니다.", Path.GetFileName(source), Path.GetFileName(dest)));
                return;
            }

            jobs.Add(new ResizeJob(source, dest, png));
        }
    }

    /// <summary>작업 목록 실행. 파일 단위로 병렬. 실패한 파일은 건너뛰고 오류 목록에 남긴다.</summary>
    public static async Task<ResizeSummary> RunAsync(IReadOnlyList<ResizeJob> jobs, ResizeOptions options, IProgress<(int Done, string Name)>? progress, CancellationToken cancellationToken)
    {
        var errors = new ConcurrentBag<string>();
        var folders = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        int done = 0, ok = 0, skipped = 0;

        await Parallel.ForEachAsync(jobs, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
            CancellationToken = cancellationToken,
        }, (job, ct) =>
        {
            try
            {
                switch (ResizeOne(job, options, ct))
                {
                    case true:
                        Interlocked.Increment(ref ok);
                        folders.TryAdd(Path.GetDirectoryName(job.Destination)!, 0);
                        break;
                    case false:
                        Interlocked.Increment(ref skipped);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException
                                           or ArgumentException or InvalidOperationException or OverflowException
                                           or System.Runtime.InteropServices.COMException)
            {
                errors.Add($"{Path.GetFileName(job.Source)}: {ex.Message}");
            }

            progress?.Report((Interlocked.Increment(ref done), Path.GetFileName(job.Source)));
            return ValueTask.CompletedTask;
        });

        return new ResizeSummary(ok, skipped, errors.ToList(), folders.Keys.ToList());
    }

    /// <summary>한 파일. true = 저장함, false = 건너뜀(이미 목표 크기 등).</summary>
    internal static bool ResizeOne(ResizeJob job, ResizeOptions options, CancellationToken cancellationToken)
    {
        // 머리글만 읽어 크기 확인 (픽셀은 아직 디코드하지 않음)
        int width, height;
        using (var probe = new FileStream(job.Source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var decoder = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            width = decoder.Frames[0].PixelWidth;
            height = decoder.Frames[0].PixelHeight;
        }

        if ((long)width * height > MaxPixels) throw new InvalidOperationException(L.F("이미지가 너무 큽니다 ({0}×{1}).", width, height));

        var (targetW, targetH) = TargetSize(width, height, options);
        var sameFile = string.Equals(job.Source, job.Destination, StringComparison.OrdinalIgnoreCase);
        if (sameFile && targetW == width && targetH == height) return false; // 덮어쓰기인데 크기가 같으면 손대지 않음
        cancellationToken.ThrowIfCancellationRequested();

        // 디코더 단계 축소: 큰 JPEG도 전체 해상도로 풀지 않아 빠르고 메모리를 적게 쓴다
        BitmapSource scaled;
        using (var stream = new FileStream(job.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            // IgnoreImageCache는 StreamSource와 함께 쓰면 WPF가 ArgumentNullException을 던진다 (스트림은 원래 캐시되지 않음)
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            image.DecodePixelWidth = targetW;
            image.DecodePixelHeight = targetH;
            image.EndInit();
            scaled = image;
        }

        scaled.Freeze();
        if (scaled.PixelWidth != targetW || scaled.PixelHeight != targetH)
        {
            // 일부 코덱은 디코더 축소를 지원하지 않는다 → 고품질 변환으로 맞춘다
            var transform = new TransformedBitmap(scaled, new System.Windows.Media.ScaleTransform(targetW / (double)scaled.PixelWidth, targetH / (double)scaled.PixelHeight));
            transform.Freeze();
            scaled = transform;
        }

        var encoder = CreateEncoder(Path.GetExtension(job.Destination), options.JpegQuality);
        encoder.Frames.Add(BitmapFrame.Create(scaled));

        Directory.CreateDirectory(Path.GetDirectoryName(job.Destination)!);
        var temp = job.Destination + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                encoder.Save(output);
            }

            // 기존 파일은 원자적으로 교체 (중간에 실패해도 원본이 깨지지 않는다)
            if (File.Exists(job.Destination)) File.Replace(temp, job.Destination, null, ignoreMetadataErrors: true);
            else File.Move(temp, job.Destination);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }

        return true;
    }

    private static BitmapEncoder CreateEncoder(string extension, int jpegQuality) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jpe" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) },
        ".bmp" => new BmpBitmapEncoder(),
        ".gif" => new GifBitmapEncoder(),
        ".tif" or ".tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
        _ => new PngBitmapEncoder(),
    };

    private static bool IsUnder(string path, string folder)
    {
        var f = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }
}

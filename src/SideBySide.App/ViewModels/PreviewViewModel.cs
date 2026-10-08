using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using SideBySide.App.Infrastructure;
using SideBySide.Core.Diff;
using SideBySide.Shell.Preview;

namespace SideBySide.App.ViewModels;

public enum PreviewKind
{
    None,
    Image,
    Text,

    /// <summary>Windows 미리보기 처리기 사용 (PDF, Office, 동영상 등). 뷰가 호스팅한다.</summary>
    Shell,
    Unsupported,
}

/// <summary>미리보기 창. 선택이 빠르게 바뀌어도 마지막 선택만 로드되도록 지연·취소한다.</summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    private const int MaxTextBytes = 256 * 1024;
    private const int ImageDecodeWidth = 1200;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp", ".jfif", ".heic",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".markdown", ".csv", ".tsv", ".ini", ".cfg", ".conf", ".config", ".json", ".jsonc", ".xml", ".yml", ".yaml",
        ".toml", ".cs", ".csx", ".xaml", ".csproj", ".props", ".targets", ".sln", ".vb", ".fs", ".c", ".h", ".cpp", ".hpp", ".cc",
        ".java", ".kt", ".go", ".rs", ".py", ".rb", ".php", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".vue", ".svelte", ".html",
        ".htm", ".css", ".scss", ".less", ".sql", ".sh", ".bash", ".ps1", ".psm1", ".bat", ".cmd", ".gitignore", ".gitattributes",
        ".editorconfig", ".env", ".dockerfile", ".gradle", ".swift", ".lua", ".r", ".m", ".pl", ".svg",
    };

    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private PreviewKind _kind;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _details = string.Empty;

    [ObservableProperty]
    private ImageSource? _image;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string? _shellPath;

    public async Task ShowAsync(FileItemViewModel? item)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();

        if (item is null || item.IsPlaceholder || item.Entry is null)
        {
            Reset(PreviewKind.None);
            return;
        }

        try
        {
            await Task.Delay(120, cts.Token); // 키보드로 빠르게 이동할 때 불필요한 로드 방지
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var entry = item.Entry;
        Title = item.Label;
        Details = item.IsDirectory
            ? item.TypeText
            : $"{item.TypeText} · {Formatting.Size(entry.Size)} · {Formatting.Date(entry.LastWriteTimeUtc)}";

        if (item.IsDirectory)
        {
            Reset(PreviewKind.None, keepTitle: true);
            return;
        }

        var ext = Path.GetExtension(entry.FullPath);
        try
        {
            if (ImageExtensions.Contains(ext) && await LoadImageAsync(entry.FullPath, cts.Token) is { } image)
            {
                if (cts.IsCancellationRequested) return;
                Reset(PreviewKind.Image, keepTitle: true);
                Image = image;
                return;
            }

            if (TextExtensions.Contains(ext) || (!PreviewHandlerRegistry.TryFind(ext, out _) && entry.Size <= MaxTextBytes))
            {
                var text = await Task.Run(() => ReadText(entry.FullPath), cts.Token);
                if (cts.IsCancellationRequested) return;
                if (text is not null)
                {
                    Reset(PreviewKind.Text, keepTitle: true);
                    Text = text;
                    return;
                }
            }

            if (PreviewHandlerRegistry.TryFind(ext, out _))
            {
                Reset(PreviewKind.Shell, keepTitle: true);
                ShellPath = entry.FullPath;
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
        }

        Reset(PreviewKind.Unsupported, keepTitle: true);
    }

    /// <summary>미리보기 처리기 호스팅이 실패했을 때 뷰가 호출.</summary>
    public void ShellPreviewFailed() => Reset(PreviewKind.Unsupported, keepTitle: true);

    public void Clear()
    {
        _cts?.Cancel();
        Reset(PreviewKind.None);
    }

    private void Reset(PreviewKind kind, bool keepTitle = false)
    {
        Kind = kind;
        Image = null;
        Text = string.Empty;
        ShellPath = null;
        if (!keepTitle)
        {
            Title = string.Empty;
            Details = string.Empty;
        }
    }

    private static string? ReadText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[Math.Min(fs.Length, MaxTextBytes)];
        fs.ReadExactly(buffer);
        var content = TextFileCodec.Decode(buffer);
        if (content.IsBinary) return null;
        var text = string.Join("\n", content.Lines);
        return fs.Length > MaxTextBytes ? text + "\n\n…" : text;
    }

    private static Task<ImageSource?> LoadImageAsync(string path, CancellationToken ct) => Task.Run<ImageSource?>(() =>
    {
        var bytes = File.ReadAllBytes(path);
        ct.ThrowIfCancellationRequested();
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelWidth = ImageDecodeWidth;
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }, ct);
}

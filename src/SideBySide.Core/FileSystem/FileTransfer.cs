using System.Diagnostics;
using SideBySide.Core.Localization;

namespace SideBySide.Core.FileSystem;

public enum TransferKind
{
    Copy,
    Move,
}

public enum ConflictChoice
{
    Overwrite,
    Skip,
    KeepBoth,
    Cancel,
}

/// <summary>대상에 같은 이름이 있을 때 사용자에게 물을 정보.</summary>
public sealed record TransferConflict(
    string SourcePath,
    string DestinationPath,
    bool SourceIsDirectory,
    bool DestinationIsDirectory,
    long SourceSize,
    DateTime SourceModifiedUtc,
    long DestinationSize,
    DateTime DestinationModifiedUtc)
{
    /// <summary>파일↔폴더처럼 종류가 달라 덮어쓸 수 없는 충돌.</summary>
    public bool CanOverwrite => !SourceIsDirectory && !DestinationIsDirectory;
}

public sealed record TransferProgress(
    TransferKind Kind,
    long BytesDone,
    long BytesTotal,
    int FilesDone,
    int FilesTotal,
    string CurrentItem,
    double BytesPerSecond)
{
    public double Fraction => BytesTotal > 0 ? Math.Min(1, (double)BytesDone / BytesTotal)
        : FilesTotal > 0 ? Math.Min(1, (double)FilesDone / FilesTotal)
        : 0;

    public TimeSpan? Remaining => BytesPerSecond > 1 && BytesTotal > BytesDone
        ? TimeSpan.FromSeconds((BytesTotal - BytesDone) / BytesPerSecond)
        : null;
}

public sealed record TransferError(string Path, string Message);

/// <summary>최상위 항목 하나의 결과. <see cref="CreatedNew"/>가 true면 대상에 새로 생긴 항목(실행 취소 대상).</summary>
public sealed record TransferredItem(string Source, string Destination, bool CreatedNew);

public sealed record TransferResult(
    IReadOnlyList<TransferredItem> Items,
    int FilesProcessed,
    int Skipped,
    IReadOnlyList<TransferError> Errors,
    bool Cancelled);

/// <summary>
/// 진행률을 보고하는 파일 복사/이동 엔진 (Windows 셸 진행 창 대신 앱 안에서 진행률을 보여주기 위해).
/// <list type="bullet">
/// <item>사전 스캔으로 전체 바이트를 계산해 바이트 기준 진행률·속도·남은 시간을 보고</item>
/// <item>작은 파일은 <see cref="File.Copy(string, string, bool)"/>(OS 최적 경로), 큰 파일은 버퍼 스트림으로 복사해 파일 안 진행률까지 보고</item>
/// <item>수정·생성 시각과 속성을 보존 (비교 모드의 크기·날짜 판정이 복사 후에도 "같음"이 되도록)</item>
/// <item>같은 볼륨 이동은 이름만 바꾸는 즉시 이동, 다른 볼륨은 복사 후 원본 삭제, 같은 이름 폴더는 병합</item>
/// <item>항목별 오류는 모아서 계속 진행, 취소 시 쓰다 만 파일은 지운다</item>
/// </list>
/// 콜백(<c>resolveConflict</c>)은 엔진 스레드에서 호출되므로 UI가 필요하면 호출 측에서 마샬링한다.
/// </summary>
public sealed class FileTransfer
{
    private const long StreamThreshold = 4L * 1024 * 1024;
    private const int BufferSize = 1024 * 1024;

    /// <summary>진행률 보고 최소 간격 (UI가 넘치지 않도록). 테스트에서는 0으로 낮춰 매 버퍼마다 보고하게 한다.</summary>
    internal static int ReportIntervalMs { get; set; } = 100;

    private readonly TransferKind _kind;
    private readonly string _destination;
    private readonly Func<TransferConflict, Task<ConflictChoice>> _resolveConflict;
    private readonly IProgress<TransferProgress>? _progress;
    private readonly CancellationToken _ct;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly List<TransferError> _errors = [];
    private readonly List<TransferredItem> _items = [];

    private long _bytesTotal;
    private long _bytesDone;
    private int _filesTotal;
    private int _filesDone;
    private int _skipped;
    private string _current = string.Empty;
    private long _lastReportMs = long.MinValue / 2;

    private FileTransfer(TransferKind kind, string destination, Func<TransferConflict, Task<ConflictChoice>> resolveConflict,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        _kind = kind;
        _destination = Path.GetFullPath(destination);
        _resolveConflict = resolveConflict;
        _progress = progress;
        _ct = ct;
    }

    public static Task<TransferResult> RunAsync(
        TransferKind kind,
        IReadOnlyList<string> sources,
        string destinationDirectory,
        Func<TransferConflict, Task<ConflictChoice>> resolveConflict,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
        => new FileTransfer(kind, destinationDirectory, resolveConflict, progress, ct).RunAsync(sources);

    private async Task<TransferResult> RunAsync(IReadOnlyList<string> sources)
    {
        var cancelled = false;
        try
        {
            if (!Directory.Exists(_destination)) throw new DirectoryNotFoundException(L.F("대상 폴더가 없습니다: {0}", _destination));
            foreach (var source in sources) Scan(Path.GetFullPath(source));
            Report(force: true);

            foreach (var raw in sources)
            {
                _ct.ThrowIfCancellationRequested();
                var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
                var isDir = Directory.Exists(source);
                if (!isDir && !File.Exists(source))
                {
                    _errors.Add(new TransferError(source, L.T("원본을 찾을 수 없습니다.")));
                    continue;
                }

                if (isDir && PathNames.IsSameOrChildOf(_destination, source))
                {
                    _errors.Add(new TransferError(source, L.T("폴더를 자기 자신이나 하위 폴더로 옮기거나 복사할 수 없습니다.")));
                    continue;
                }

                var name = Path.GetFileName(source);
                var target = Path.Combine(_destination, name);
                if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                {
                    if (_kind == TransferKind.Move)
                    {
                        _skipped++;
                        continue;
                    }

                    // 같은 폴더에 붙여넣기: 탐색기처럼 "이름 - 복사본"
                    target = isDir
                        ? PathNames.MakeUnique(_destination, name + L.T(" - 복사본"))
                        : PathNames.MakeUnique(_destination, Path.GetFileNameWithoutExtension(name) + L.T(" - 복사본"), Path.GetExtension(name));
                }

                var existedBefore = File.Exists(target) || Directory.Exists(target);
                var final = isDir
                    ? await TransferDirectoryAsync(source, target).ConfigureAwait(false)
                    : await TransferFileAsync(source, target).ConfigureAwait(false);
                if (final is not null)
                    _items.Add(new TransferredItem(source, final, !existedBefore || !string.Equals(final, target, StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _errors.Add(new TransferError(_destination, ex.Message));
        }

        Report(force: true);
        return new TransferResult(_items, _filesDone, _skipped, _errors, cancelled);
    }

    // ── 스캔 ────────────────────────────────────────────────────────

    private void Scan(string path)
    {
        if (File.Exists(path))
        {
            _filesTotal++;
            _bytesTotal += new FileInfo(path).Length;
            return;
        }

        if (!Directory.Exists(path)) return;
        var (files, bytes) = CountTree(path);
        _filesTotal += files;
        _bytesTotal += bytes;
    }

    private (int Files, long Bytes) CountTree(string directory)
    {
        int files = 0;
        long bytes = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", options))
        {
            _ct.ThrowIfCancellationRequested();
            files++;
            bytes += file.Length;
        }

        return (files, bytes);
    }

    // ── 파일 ────────────────────────────────────────────────────────

    /// <summary>대상 경로를 돌려준다. 건너뛰었거나 실패하면 null.</summary>
    private async Task<string?> TransferFileAsync(string source, string target)
    {
        _ct.ThrowIfCancellationRequested();
        var info = new FileInfo(source);
        var length = info.Length; // FileInfo는 처음 읽을 때 값을 가져오므로 이동 전에 확보
        _current = info.Name;
        Report(force: false);

        if (File.Exists(target) || Directory.Exists(target))
        {
            var resolved = await ResolveAsync(source, target, sourceIsDirectory: false).ConfigureAwait(false);
            if (resolved is null)
            {
                _skipped++;
                _filesDone++;
                _bytesDone += length;
                Report(force: false);
                return null;
            }

            target = resolved;
        }

        try
        {
            if (_kind == TransferKind.Move && SameVolume(source, target))
            {
                ClearReadOnly(target);
                File.Move(source, target, overwrite: true);
                _bytesDone += length;
            }
            else
            {
                await CopyFileAsync(info, target).ConfigureAwait(false);
                if (_kind == TransferKind.Move)
                {
                    ClearReadOnly(source);
                    File.Delete(source);
                }
            }

            _filesDone++;
            Report(force: false);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _errors.Add(new TransferError(source, ex.Message));
            _filesDone++;
            return null;
        }
    }

    private async Task CopyFileAsync(FileInfo source, string target)
    {
        ClearReadOnly(target);
        if (source.Length < StreamThreshold)
        {
            File.Copy(source.FullName, target, overwrite: true);
            _bytesDone += source.Length;
        }
        else
        {
            try
            {
                var read = new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, Options = FileOptions.SequentialScan | FileOptions.Asynchronous, BufferSize = 0 };
                var write = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous, BufferSize = 0, PreallocationSize = source.Length };
                await using var input = new FileStream(source.FullName, read);
                await using var output = new FileStream(target, write);
                var buffer = new byte[BufferSize];
                int n;
                while ((n = await input.ReadAsync(buffer, _ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), _ct).ConfigureAwait(false);
                    _bytesDone += n;
                    Report(force: false);
                }
            }
            catch
            {
                TryDelete(target);
                throw;
            }
        }

        // 시각·속성 보존 (File.Copy는 수정 시각을 유지하지만 스트림 복사는 아니므로 항상 맞춘다)
        File.SetCreationTimeUtc(target, source.CreationTimeUtc);
        File.SetLastWriteTimeUtc(target, source.LastWriteTimeUtc);
        File.SetAttributes(target, source.Attributes);
    }

    // ── 폴더 ────────────────────────────────────────────────────────

    private async Task<string?> TransferDirectoryAsync(string source, string target)
    {
        _ct.ThrowIfCancellationRequested();
        _current = Path.GetFileName(source);
        Report(force: false);

        if (File.Exists(target))
        {
            var resolved = await ResolveAsync(source, target, sourceIsDirectory: true).ConfigureAwait(false);
            if (resolved is null)
            {
                var (files, bytes) = CountTree(source);
                _skipped += files;
                _filesDone += files;
                _bytesDone += bytes;
                return null;
            }

            target = resolved;
        }

        try
        {
            // 같은 볼륨에서 대상 폴더가 없으면 통째로 이름만 바꿔 즉시 이동
            if (_kind == TransferKind.Move && !Directory.Exists(target) && SameVolume(source, target))
            {
                var (files, bytes) = CountTree(source);
                Directory.Move(source, target);
                _filesDone += files;
                _bytesDone += bytes;
                Report(force: false);
                return target;
            }

            var createdNew = !Directory.Exists(target);
            Directory.CreateDirectory(target);

            foreach (var child in Directory.GetFileSystemEntries(source))
            {
                var childTarget = Path.Combine(target, Path.GetFileName(child));
                if (Directory.Exists(child)) await TransferDirectoryAsync(child, childTarget).ConfigureAwait(false);
                else await TransferFileAsync(child, childTarget).ConfigureAwait(false);
            }

            var sourceInfo = new DirectoryInfo(source);
            if (createdNew)
            {
                Directory.SetCreationTimeUtc(target, sourceInfo.CreationTimeUtc);
                Directory.SetLastWriteTimeUtc(target, sourceInfo.LastWriteTimeUtc);
            }

            // 이동: 모두 옮겨졌으면(건너뛴 항목이 없으면) 빈 원본 폴더 삭제
            if (_kind == TransferKind.Move && !Directory.EnumerateFileSystemEntries(source).Any())
                Directory.Delete(source);

            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _errors.Add(new TransferError(source, ex.Message));
            return null;
        }
    }

    // ── 도우미 ──────────────────────────────────────────────────────

    /// <summary>충돌 처리. 사용할 대상 경로를 돌려주고, 건너뛰면 null, 취소면 예외.</summary>
    private async Task<string?> ResolveAsync(string source, string target, bool sourceIsDirectory)
    {
        var targetIsDir = Directory.Exists(target);
        FileSystemInfo s = sourceIsDirectory ? new DirectoryInfo(source) : new FileInfo(source);
        FileSystemInfo t = targetIsDir ? new DirectoryInfo(target) : new FileInfo(target);
        var conflict = new TransferConflict(
            source, target, sourceIsDirectory, targetIsDir,
            s is FileInfo sf ? sf.Length : 0, s.LastWriteTimeUtc,
            t is FileInfo tf ? tf.Length : 0, t.LastWriteTimeUtc);

        var choice = await _resolveConflict(conflict).ConfigureAwait(false);
        switch (choice)
        {
            case ConflictChoice.Cancel:
                throw new OperationCanceledException();
            case ConflictChoice.Skip:
                return null;
            case ConflictChoice.Overwrite when conflict.CanOverwrite:
                return target;
            default:
                // 둘 다 유지 (또는 덮어쓸 수 없는 종류 충돌): "이름 (2).ext"
                var dir = Path.GetDirectoryName(target)!;
                var name = Path.GetFileName(target);
                return sourceIsDirectory
                    ? PathNames.MakeUnique(dir, name)
                    : PathNames.MakeUnique(dir, Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
        }
    }

    private void Report(bool force)
    {
        if (_progress is null) return;
        var now = _watch.ElapsedMilliseconds;
        if (!force && now - _lastReportMs < ReportIntervalMs) return;
        _lastReportMs = now;
        var seconds = Math.Max(0.001, _watch.Elapsed.TotalSeconds);
        _progress.Report(new TransferProgress(_kind, _bytesDone, _bytesTotal, _filesDone, _filesTotal, _current, _bytesDone / seconds));
    }

    private static bool SameVolume(string a, string b)
        => string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static void ClearReadOnly(string path)
    {
        if (!File.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

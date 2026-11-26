using SideBySide.Core.FileSystem;
using SideBySide.Core.Filtering;

namespace SideBySide.Core.Compare;

/// <summary>비교 복사에서 복사할 파일 하나. <see cref="Overwrites"/>는 대상의 더 오래된 같은 이름 파일을 덮어쓰는지.</summary>
public sealed record CompareCopyFile(string Source, string Target, bool Overwrites);

/// <summary>
/// 비교 복사 계획. <see cref="Directories"/>는 원본에만 있어 새로 만들 폴더(빈 폴더 포함, 상위가 먼저).
/// <see cref="NotNewer"/>는 내용은 다르지만 원본 쪽이 더 최신이 아니어서 건너뛴 파일 수.
/// </summary>
public sealed record CompareCopyPlan(
    IReadOnlyList<CompareCopyFile> Files,
    IReadOnlyList<(string Source, string Target)> Directories,
    int NotNewer,
    int Errors)
{
    public bool IsEmpty => Files.Count == 0 && Directories.Count == 0;
}

/// <summary>
/// 비교 모드에서 한쪽 창의 선택 항목을 반대쪽으로 복사할 때 실제로 복사할 파일을 고른다.
/// <list type="bullet">
/// <item>대상에 없는 파일·폴더(원본에만 있음): 모두 복사 (폴더는 필터에 보이는 하위 항목 전체)</item>
/// <item>양쪽에 있는 파일: 비교 결과가 다르고 원본이 더 최신일 때만 복사 (같거나, 대상이 더 최신이거나, 시각이 같으면 건너뜀)</item>
/// <item>양쪽에 있는 폴더: 하위를 같은 기준으로 재귀 비교해 위 조건에 맞는 파일만 같은 상대 경로로 복사</item>
/// </list>
/// 비교 화면과 같은 필터·비교 옵션을 쓴다 (필터에 숨겨진 항목은 복사하지 않는다).
/// </summary>
public static class CompareCopyPlanner
{
    public static Task<CompareCopyPlan> BuildAsync(
        IReadOnlyList<string> sources, string destinationDirectory, FilterSet filter, CompareOptions options, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            var builder = new Builder(filter, options, ct);
            foreach (var raw in sources)
            {
                ct.ThrowIfCancellationRequested();
                var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
                var target = Path.Combine(destinationDirectory, Path.GetFileName(source));
                if (Directory.Exists(source))
                {
                    if (Directory.Exists(target)) await builder.CompareDirectoryAsync(source, target).ConfigureAwait(false);
                    else builder.AddWholeDirectory(source, target);
                }
                else if (File.Exists(source))
                {
                    if (File.Exists(target)) await builder.CompareFileAsync(FileEntry.FromPath(source), FileEntry.FromPath(target)).ConfigureAwait(false);
                    else builder.AddFile(source, target, overwrites: false);
                }
            }

            return builder.ToPlan();
        }, ct);

    private sealed class Builder(FilterSet filter, CompareOptions options, CancellationToken ct)
    {
        private readonly List<CompareCopyFile> _files = [];
        private readonly List<(string Source, string Target)> _directories = [];
        private int _notNewer;
        private int _errors;

        public CompareCopyPlan ToPlan() => new(_files, _directories, _notNewer, _errors);

        public void AddFile(string source, string target, bool overwrites) => _files.Add(new CompareCopyFile(source, target, overwrites));

        /// <summary>대상에 없는 폴더: 폴더를 만들고 필터에 보이는 하위 항목을 모두 복사.</summary>
        public void AddWholeDirectory(string source, string target)
        {
            ct.ThrowIfCancellationRequested();
            _directories.Add((source, target));
            List<FileEntry> entries;
            try
            {
                entries = DirectoryLoader.Load(source, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _errors++;
                return;
            }

            foreach (var e in entries.Where(filter.IsVisible))
            {
                var childTarget = Path.Combine(target, e.Name);
                if (e.IsDirectory) AddWholeDirectory(e.FullPath, childTarget);
                else AddFile(e.FullPath, childTarget, overwrites: false);
            }
        }

        public async Task CompareDirectoryAsync(string source, string target)
        {
            ct.ThrowIfCancellationRequested();
            List<CompareRow> rows;
            try
            {
                rows = FolderComparer.Merge(DirectoryLoader.Load(source, ct), DirectoryLoader.Load(target, ct), filter);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _errors++;
                return;
            }

            // Merge의 Left = 원본, Right = 대상
            foreach (var row in rows)
            {
                if (row.Left is null) continue; // 대상에만 있음
                var childTarget = Path.Combine(target, row.Left.Name);
                if (row.Right is null)
                {
                    if (row.IsDirectory) AddWholeDirectory(row.Left.FullPath, childTarget);
                    else AddFile(row.Left.FullPath, childTarget, overwrites: false);
                }
                else if (row.IsDirectory)
                {
                    await CompareDirectoryAsync(row.Left.FullPath, row.Right.FullPath).ConfigureAwait(false);
                }
                else
                {
                    await CompareFileAsync(row.Left, row.Right).ConfigureAwait(false);
                }
            }
        }

        public async Task CompareFileAsync(FileEntry source, FileEntry target)
        {
            var (state, newer) = await FolderComparer.CompareFilesAsync(source, target, options, ct).ConfigureAwait(false);
            switch (state)
            {
                case CompareState.Same:
                    return;
                case CompareState.Error:
                    _errors++;
                    return;
            }

            if (newer == NewerSide.Left) AddFile(source.FullPath, target.FullPath, overwrites: true);
            else _notNewer++;
        }
    }
}

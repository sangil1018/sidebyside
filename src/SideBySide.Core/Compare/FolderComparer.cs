using System.Collections.Concurrent;
using SideBySide.Core.FileSystem;
using SideBySide.Core.Filtering;

namespace SideBySide.Core.Compare;

/// <summary>
/// 폴더 비교 엔진.
/// <list type="number">
/// <item><see cref="CompareLevelAsync"/>: 한 단계(현재 폴더)를 열거·정렬·병합하고 파일 상태를 판정 (빠름).</item>
/// <item><see cref="ResolveDirectoriesAsync"/>: 양쪽에 있는 하위 폴더의 상태를 병렬로 재귀 계산하여 콜백으로 알림.</item>
/// </list>
/// 폴더 상태는 캐시되어 하위 폴더로 들어갈 때 다시 계산하지 않는다. 옵션/필터가 바뀌면 <see cref="ClearCache"/>.
/// </summary>
public sealed class FolderComparer
{
    private readonly ConcurrentDictionary<string, CompareState> _dirCache = new(StringComparer.OrdinalIgnoreCase);

    public void ClearCache() => _dirCache.Clear();

    public async Task<IReadOnlyList<CompareRow>> CompareLevelAsync(
        string leftDir, string rightDir, FilterSet filter, CompareOptions options, CancellationToken ct = default)
    {
        var leftTask = Task.Run(() => DirectoryLoader.LoadOrEmpty(leftDir, ct), ct);
        var rightTask = Task.Run(() => DirectoryLoader.LoadOrEmpty(rightDir, ct), ct);
        await Task.WhenAll(leftTask, rightTask).ConfigureAwait(false);

        var rows = Merge(leftTask.Result, rightTask.Result, filter);

        var parallel = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount };
        await Parallel.ForEachAsync(
            rows.Where(r => !r.IsDirectory && !r.IsOneSided),
            parallel,
            async (row, token) => (row.State, row.Newer) = await CompareFilesAsync(row.Left!, row.Right!, options, token).ConfigureAwait(false))
            .ConfigureAwait(false);

        foreach (var row in rows.Where(r => r.IsDirectory && !r.IsOneSided))
        {
            if (!options.CompareSubfolders) row.State = CompareState.Same;
            else if (_dirCache.TryGetValue(Key(row.Left!.FullPath, row.Right!.FullPath), out var cached)) row.State = cached;
        }

        return rows;
    }

    /// <summary>Pending 상태인 폴더 행들을 병렬로 계산. 각 행이 끝날 때마다 <paramref name="onResolved"/> 호출 (임의 스레드).</summary>
    public Task ResolveDirectoriesAsync(
        IReadOnlyList<CompareRow> rows, FilterSet filter, CompareOptions options,
        Action<CompareRow> onResolved, CancellationToken ct = default)
    {
        var pending = rows.Where(r => r.IsDirectory && r.State == CompareState.Pending).ToList();
        if (pending.Count == 0) return Task.CompletedTask;

        var parallel = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount };
        return Parallel.ForEachAsync(pending, parallel, async (row, token) =>
        {
            row.State = await GetDirectoryStateAsync(row.Left!.FullPath, row.Right!.FullPath, filter, options, token).ConfigureAwait(false);
            onResolved(row);
        });
    }

    /// <summary>두 폴더 트리가 같은지 재귀적으로 판정. 첫 차이에서 중단.</summary>
    public async Task<CompareState> GetDirectoryStateAsync(
        string leftDir, string rightDir, FilterSet filter, CompareOptions options, CancellationToken ct)
    {
        var key = Key(leftDir, rightDir);
        if (_dirCache.TryGetValue(key, out var cached)) return cached;

        CompareState state;
        try
        {
            var rows = Merge(DirectoryLoader.LoadOrEmpty(leftDir, ct), DirectoryLoader.LoadOrEmpty(rightDir, ct), filter);
            state = CompareState.Same;
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                CompareState rowState;
                if (row.IsOneSided) rowState = CompareState.Different;
                else if (row.IsDirectory) rowState = await GetDirectoryStateAsync(row.Left!.FullPath, row.Right!.FullPath, filter, options, ct).ConfigureAwait(false);
                else rowState = (await CompareFilesAsync(row.Left!, row.Right!, options, ct).ConfigureAwait(false)).State;

                if (rowState != CompareState.Same)
                {
                    state = rowState == CompareState.Error ? CompareState.Error : CompareState.Different;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state = CompareState.Error;
        }

        _dirCache[key] = state;
        return state;
    }

    public static async Task<(CompareState State, NewerSide Newer)> CompareFilesAsync(
        FileEntry left, FileEntry right, CompareOptions options, CancellationToken ct = default)
    {
        var newer = TimesEqual(left.LastWriteTimeUtc, right.LastWriteTimeUtc, options) ? NewerSide.None
                  : left.LastWriteTimeUtc > right.LastWriteTimeUtc ? NewerSide.Left
                  : NewerSide.Right;

        if (left.Size != right.Size) return (CompareState.Different, newer);

        if (options.Method == CompareMethod.SizeAndDate)
            return newer == NewerSide.None ? (CompareState.Same, newer) : (CompareState.Different, newer);

        try
        {
            return await FileContentComparer.AreEqualAsync(left.FullPath, right.FullPath, ct).ConfigureAwait(false)
                ? (CompareState.Same, NewerSide.None)
                : (CompareState.Different, newer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (CompareState.Error, newer);
        }
    }

    public static bool TimesEqual(DateTime a, DateTime b, CompareOptions options)
    {
        var diff = Math.Abs((a - b).TotalSeconds);
        if (diff <= options.TimeToleranceSeconds) return true;
        return options.IgnoreHourOffset && Math.Abs(diff - 3600) <= options.TimeToleranceSeconds;
    }

    /// <summary>양쪽 목록을 이름(대소문자 무시) + 종류로 맞춰 행 목록을 만든다. 폴더 먼저, 자연 정렬.</summary>
    public static List<CompareRow> Merge(IEnumerable<FileEntry> left, IEnumerable<FileEntry> right, FilterSet filter)
    {
        var map = new Dictionary<(string, bool), (FileEntry? L, FileEntry? R)>(KeyComparer.Instance);
        foreach (var e in left.Where(filter.IsVisible))
            map[(e.Name, e.IsDirectory)] = (e, null);
        foreach (var e in right.Where(filter.IsVisible))
            map[(e.Name, e.IsDirectory)] = map.TryGetValue((e.Name, e.IsDirectory), out var pair) ? (pair.L, e) : (null, e);

        return map
            .Select(kv => new CompareRow(kv.Value.L?.Name ?? kv.Value.R!.Name, kv.Key.Item2, kv.Value.L, kv.Value.R))
            .OrderByDescending(r => r.IsDirectory)
            .ThenBy(r => r.Name, NaturalStringComparer.Instance)
            .ToList();
    }

    private static string Key(string left, string right) => left + "\u0001" + right;

    private sealed class KeyComparer : IEqualityComparer<(string Name, bool IsDir)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string Name, bool IsDir) x, (string Name, bool IsDir) y)
            => x.IsDir == y.IsDir && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Name, bool IsDir) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name), obj.IsDir);
    }
}

using System.IO.Enumeration;

namespace SideBySide.Core.FileSystem;

/// <summary>
/// 디렉터리 한 단계를 빠르게 열거한다.
/// FileSystemEnumerable은 Win32 FindFirstFileEx 위에서 FileInfo 할당 없이 동작하므로 일반 API보다 훨씬 빠르다.
/// </summary>
public static class DirectoryLoader
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    public static List<FileEntry> Load(string path, CancellationToken ct = default)
    {
        var enumerable = new FileSystemEnumerable<FileEntry>(
            path,
            (ref FileSystemEntry e) => new FileEntry(
                e.FileName.ToString(),
                e.ToFullPath(),
                e.IsDirectory,
                e.IsDirectory ? 0 : e.Length,
                e.LastWriteTimeUtc.UtcDateTime,
                e.Attributes),
            Options);

        var list = new List<FileEntry>();
        foreach (var entry in enumerable)
        {
            ct.ThrowIfCancellationRequested();
            list.Add(entry);
        }

        return list;
    }

    /// <summary>존재하지 않는 폴더는 빈 목록을 돌려준다 (비교 모드에서 한쪽에만 있는 폴더 진입 시).</summary>
    public static List<FileEntry> LoadOrEmpty(string path, CancellationToken ct = default)
        => Directory.Exists(path) ? Load(path, ct) : [];

    public static Task<List<FileEntry>> LoadAsync(string path, CancellationToken ct = default)
        => Task.Run(() => Load(path, ct), ct);

    /// <summary>
    /// 하위 폴더까지 재귀 검색. <paramref name="recurseInto"/>가 false인 폴더(필터로 제외된 폴더 등)는 들어가지 않고,
    /// 심볼릭 링크/정션은 순환을 막기 위해 따라가지 않는다. 결과는 찾는 대로 스트리밍된다.
    /// </summary>
    public static IEnumerable<FileEntry> Search(
        string root,
        Func<string, bool, FileAttributes, bool> include,
        Func<string, FileAttributes, bool> recurseInto,
        CancellationToken ct = default)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false,
            BufferSize = 64 * 1024,
        };

        var enumerable = new FileSystemEnumerable<FileEntry>(
            root,
            (ref FileSystemEntry e) => new FileEntry(
                e.FileName.ToString(),
                e.ToFullPath(),
                e.IsDirectory,
                e.IsDirectory ? 0 : e.Length,
                e.LastWriteTimeUtc.UtcDateTime,
                e.Attributes),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => include(e.FileName.ToString(), e.IsDirectory, e.Attributes),
            ShouldRecursePredicate = (ref FileSystemEntry e) =>
                (e.Attributes & FileAttributes.ReparsePoint) == 0 && recurseInto(e.FileName.ToString(), e.Attributes),
        };

        foreach (var entry in enumerable)
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
        }
    }
}

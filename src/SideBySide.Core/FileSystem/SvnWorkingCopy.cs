namespace SideBySide.Core.FileSystem;

/// <summary>
/// SVN 작업 사본 판별. svn을 실행하지 않고 폴더 구조만 본다 (메뉴·자동 열기 판단처럼 자주 불리는 곳에서 쓴다).
/// SVN 1.7부터는 작업 사본 최상위 폴더에만 <c>.svn</c> 폴더가 있다.
/// </summary>
public static class SvnWorkingCopy
{
    /// <summary><paramref name="path"/>(폴더 또는 파일)가 속한 작업 사본의 최상위 폴더. 작업 사본 안이 아니면 null.</summary>
    public static string? FindRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var current = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path).Directory;
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".svn"))) return current.FullName;
                current = current.Parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            // 읽을 수 없는 위치는 작업 사본이 아닌 것으로 본다
        }

        return null;
    }
}

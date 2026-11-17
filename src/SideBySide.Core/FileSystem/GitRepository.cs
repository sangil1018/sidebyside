namespace SideBySide.Core.FileSystem;

/// <summary>
/// git 저장소 판별. git을 실행하지 않고 폴더 구조만 본다 (메뉴·자동 열기 판단처럼 자주 불리는 곳에서 쓴다).
/// </summary>
public static class GitRepository
{
    /// <summary>
    /// <paramref name="path"/>(폴더 또는 파일)가 속한 작업 트리의 최상위 폴더. 저장소 안이 아니면 null.
    /// <c>.git</c>이 폴더(일반 저장소)든 파일(워크트리·서브모듈)이든 저장소로 본다.
    /// </summary>
    public static string? FindRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var current = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path).Directory;
            while (current is not null)
            {
                var marker = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(marker) || File.Exists(marker)) return current.FullName;
                current = current.Parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            // 읽을 수 없는 위치는 저장소가 아닌 것으로 본다
        }

        return null;
    }

    /// <summary>같은 저장소 경로인지 (끝의 구분자·대소문자 무시).</summary>
    public static bool SameRoot(string? a, string? b)
        => a is not null && b is not null
           && string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

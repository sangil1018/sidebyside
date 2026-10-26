using System.Runtime.InteropServices;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.TempCleaner;

public sealed record CleanResult(int DeletedFiles, long FreedBytes, int SkippedFiles);

public sealed record CleanFile(string Path, long Size, DateTime Modified);

/// <summary>정리 대상 카테고리 하나.</summary>
public sealed class CleanTarget
{
    /// <summary>고정 ID. 승격 작업은 경로가 아니라 이 ID만 받는다.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Group { get; init; }

    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>삭제할 파일 패턴. 기본은 전체.</summary>
    public string FilePattern { get; init; } = "*";

    public bool IsRecycleBin { get; init; }

    /// <summary>기본 체크 (최근 항목·휴지통처럼 민감한 것은 해제).</summary>
    public bool DefaultChecked { get; init; } = true;

    /// <summary>시스템 폴더라 정리에 관리자 권한이 필요하다.</summary>
    public bool RequiresAdmin { get; init; }
}

/// <summary>
/// TempCleaner의 Cleaner 이식. 원본과 다른 점:
/// <list type="bullet">
/// <item>정션·심볼릭 링크 폴더를 따라가지 않는다 (원본은 링크를 따라가 대상 밖 파일을 지울 수 있었다).</item>
/// <item>개별 파일 삭제는 해당 카테고리 폴더 안의 파일만 허용한다.</item>
/// <item>삭제는 핸들로 열어 실제 위치를 확인한 뒤 그 핸들로 한다 (<see cref="SecureDelete"/>, 검사·삭제 사이 경로 바꿔치기 차단).</item>
/// <item>취소·진행률을 지원하고, 시스템 카테고리는 승격 프로세스에서 ID로만 정리한다.</item>
/// </list>
/// </summary>
public static class Cleaner
{
    public static List<CleanTarget> BuildTargets()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var targets = new List<CleanTarget>
        {
            new() { Id = "user-temp", Name = L.T("사용자 임시 파일"), Group = L.T("임시 파일"), Paths = [Path.GetTempPath().TrimEnd('\\')] },
            new() { Id = "windows-temp", Name = L.T("Windows 임시 파일"), Group = L.T("임시 파일"), Paths = [Path.Combine(windows, "Temp")], RequiresAdmin = true },
            new() { Id = "windows-update", Name = L.T("Windows 업데이트 캐시"), Group = L.T("시스템 캐시"), Paths = [Path.Combine(windows, "SoftwareDistribution", "Download")], RequiresAdmin = true },
            new() { Id = "inetcache", Name = L.T("시스템 인터넷 캐시 (INetCache)"), Group = L.T("시스템 캐시"), Paths = [Path.Combine(local, @"Microsoft\Windows\INetCache")] },
            new() { Id = "thumbcache", Name = L.T("썸네일 캐시"), Group = L.T("시스템 캐시"), Paths = [Path.Combine(local, @"Microsoft\Windows\Explorer")], FilePattern = "thumbcache*.db" },
            new() { Id = "d3dscache", Name = L.T("DirectX 셰이더 캐시"), Group = L.T("시스템 캐시"), Paths = [Path.Combine(local, "D3DSCache")] },
            new() { Id = "crashdumps", Name = L.T("크래시 덤프"), Group = L.T("시스템 캐시"), Paths = [Path.Combine(local, "CrashDumps")] },
            new() { Id = "chrome", Name = L.T("Chrome 캐시"), Group = L.T("브라우저 캐시"), Paths = BrowserCachePaths(Path.Combine(local, @"Google\Chrome\User Data")) },
            new() { Id = "edge", Name = L.T("Edge 캐시"), Group = L.T("브라우저 캐시"), Paths = BrowserCachePaths(Path.Combine(local, @"Microsoft\Edge\User Data")) },
            new() { Id = "recent", Name = L.T("최근 항목 (Recent)"), Group = L.T("개인 정보"), Paths = [Path.Combine(roaming, @"Microsoft\Windows\Recent")], DefaultChecked = false },
            new() { Id = "recyclebin", Name = L.T("휴지통"), Group = L.T("개인 정보"), IsRecycleBin = true, DefaultChecked = false },
        };

        return targets.Where(t => t.IsRecycleBin || t.Paths.Any(Directory.Exists)).ToList();
    }

    /// <summary>Chrome/Edge 계열 프로필별 캐시 폴더.</summary>
    internal static string[] BrowserCachePaths(string userData)
    {
        if (!Directory.Exists(userData)) return [];
        var result = new List<string>();
        try
        {
            foreach (var profile in Directory.GetDirectories(userData))
            {
                var name = Path.GetFileName(profile);
                if (name != "Default" && !name.StartsWith("Profile", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var sub in new[]
                         {
                             Path.Combine(profile, "Cache", "Cache_Data"),
                             Path.Combine(profile, "Code Cache"),
                             Path.Combine(profile, "GPUCache"),
                             Path.Combine(profile, "Service Worker", "CacheStorage"),
                         })
                {
                    if (Directory.Exists(sub)) result.Add(sub);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return result.ToArray();
    }

    private static bool IsOldEnough(FileInfo file, int minAgeDays)
    {
        if (minAgeDays <= 0) return true;
        try { return file.LastWriteTimeUtc <= DateTime.UtcNow.AddDays(-minAgeDays); }
        catch (IOException) { return false; }
    }

    /// <summary>파일 수·용량 (삭제하지 않음).</summary>
    public static (int Count, long Bytes) Scan(CleanTarget target, int minAgeDays, CancellationToken cancellationToken = default)
    {
        if (target.IsRecycleBin)
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBin(null, ref info) == 0 ? ((int)Math.Min(info.i64NumItems, int.MaxValue), info.i64Size) : (0, 0);
        }

        long bytes = 0;
        var count = 0;
        foreach (var root in target.Paths)
        {
            foreach (var file in SafeEnumerateFiles(root, target.FilePattern, cancellationToken))
            {
                if (!IsOldEnough(file, minAgeDays)) continue;
                try
                {
                    bytes += file.Length;
                    count++;
                }
                catch (IOException)
                {
                    // 그사이 사라진 파일
                }
            }
        }

        return (count, bytes);
    }

    /// <summary>카테고리의 파일 (큰 것부터, 최대 <paramref name="maxCount"/>개).</summary>
    public static (List<CleanFile> Files, bool Truncated) ListFiles(CleanTarget target, int minAgeDays, int maxCount, CancellationToken cancellationToken = default)
    {
        var files = new List<CleanFile>();
        foreach (var root in target.Paths)
        {
            foreach (var file in SafeEnumerateFiles(root, target.FilePattern, cancellationToken))
            {
                if (!IsOldEnough(file, minAgeDays)) continue;
                try { files.Add(new CleanFile(file.FullName, file.Length, file.LastWriteTime)); }
                catch (IOException) { }
            }
        }

        files.Sort((a, b) => b.Size.CompareTo(a.Size));
        var truncated = files.Count > maxCount;
        if (truncated) files.RemoveRange(maxCount, files.Count - maxCount);
        return (files, truncated);
    }

    /// <summary>개별 파일 삭제. 카테고리 폴더 밖의 경로·링크는 거부한다. 잠긴 파일은 건너뛴다.</summary>
    public static CleanResult DeleteFiles(CleanTarget target, IEnumerable<string> paths)
    {
        int deleted = 0, skipped = 0;
        long freed = 0;
        var roots = target.Paths.Select(r => Path.GetFullPath(r).TrimEnd('\\') + "\\").ToList();
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            if (!roots.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) || HasReparseParent(full, roots))
            {
                skipped++;
                continue;
            }

            var root = target.Paths.First(r => full.StartsWith(Path.GetFullPath(r).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            if (SecureDelete.FinalDirectoryPath(root) is { } finalRoot && SecureDelete.TryDelete(full, finalRoot, out var len))
            {
                freed += len;
                deleted++;
            }
            else
            {
                skipped++;
            }
        }

        return new CleanResult(deleted, freed, skipped);
    }

    /// <summary>카테고리 정리. 잠긴/사용 중 파일은 건너뛴다.</summary>
    public static CleanResult Clean(CleanTarget target, int minAgeDays, CancellationToken cancellationToken = default)
    {
        if (target.IsRecycleBin)
        {
            var (count, bytes) = Scan(target, 0, cancellationToken);
            var hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            return hr == 0 ? new CleanResult(count, bytes, 0) : new CleanResult(0, 0, count);
        }

        int deleted = 0, skipped = 0;
        long freed = 0;
        foreach (var root in target.Paths)
        {
            // 루트의 실제 경로 (루트 자체가 다른 드라이브로 옮겨진 Temp 같은 경우도 그 실제 위치 기준)
            if (SecureDelete.FinalDirectoryPath(root) is not { } finalRoot) continue;
            foreach (var file in SafeEnumerateFiles(root, target.FilePattern, cancellationToken))
            {
                if (!IsOldEnough(file, minAgeDays)) continue;
                if (SecureDelete.TryDelete(file.FullName, finalRoot, out var len))
                {
                    freed += len;
                    deleted++;
                }
                else
                {
                    skipped++;
                }
            }

            RemoveEmptySubdirectories(root);
        }

        return new CleanResult(deleted, freed, skipped);
    }


    /// <summary>
    /// 접근할 수 없는 폴더를 건너뛰며 파일을 나열한다. 정션·심볼릭 링크 폴더는 따라가지 않는다
    /// (예: Temp 안의 링크가 다른 드라이브·사용자 폴더를 가리켜도 그 안을 지우지 않음).
    /// </summary>
    internal static IEnumerable<FileInfo> SafeEnumerateFiles(string root, string pattern, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root)) yield break;

        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            DirectoryInfo[] subDirs;
            try { subDirs = dir.GetDirectories(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }

            foreach (var sub in subDirs)
            {
                if ((sub.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(sub);
            }

            FileInfo[] files;
            try { files = dir.GetFiles(pattern); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }

            foreach (var f in files) yield return f;
        }
    }

    /// <summary>정리 후 비게 된 하위 폴더 제거 (루트 유지, 링크 폴더는 건드리지 않음).</summary>
    internal static void RemoveEmptySubdirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var dir in new DirectoryInfo(root).GetDirectories())
            {
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                RemoveEmptySubdirectories(dir.FullName);
                try
                {
                    if (!dir.EnumerateFileSystemInfos().Any()) dir.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 사용 중인 폴더는 유지
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>루트와 파일 사이 폴더 중 링크가 있으면 true.</summary>
    private static bool HasReparseParent(string fullPath, IReadOnlyList<string> roots)
    {
        var root = roots.First(r => fullPath.StartsWith(r, StringComparison.OrdinalIgnoreCase)).TrimEnd('\\');
        var dir = Path.GetDirectoryName(fullPath);
        while (dir is not null && dir.Length > root.Length)
        {
            try
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return false;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    private const uint SHERB_NOCONFIRMATION = 0x1;
    private const uint SHERB_NOPROGRESSUI = 0x2;
    private const uint SHERB_NOSOUND = 0x4;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
}

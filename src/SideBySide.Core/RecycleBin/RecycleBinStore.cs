using System.Text;

namespace SideBySide.Core.RecycleBin;

/// <summary>휴지통 항목 하나: <c>$I…</c> 메타데이터 파일과 실제 내용인 <c>$R…</c> 파일/폴더.</summary>
public sealed record RecycledItem(string OriginalPath, DateTime DeletedUtc, long Size, string IndexFile, string DataPath);

/// <summary>
/// Windows 휴지통 폴더(<c>&lt;드라이브&gt;\$Recycle.Bin\&lt;사용자 SID&gt;</c>)를 직접 읽어 항목을 원래 위치로 복원한다.
/// 셸 API 없이 파일 이동만으로 동작하므로 결과가 확정적이고 테스트할 수 있다.
/// </summary>
public static class RecycleBinStore
{
    /// <summary>
    /// <c>$I</c> 파일 해석. v1(Vista~8.1): 헤더 24바이트 + 고정 260자 경로. v2(Windows 10+): 헤더 24바이트 + 길이(4바이트) + 경로.
    /// 형식이 맞지 않으면 null.
    /// </summary>
    public static (string OriginalPath, DateTime DeletedUtc, long Size)? ParseIndex(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24) return null;
        var version = BitConverter.ToInt64(data[..8]);
        var size = BitConverter.ToInt64(data[8..16]);
        var fileTime = BitConverter.ToInt64(data[16..24]);
        DateTime deleted;
        try { deleted = DateTime.FromFileTimeUtc(fileTime); }
        catch (ArgumentOutOfRangeException) { return null; }

        string path;
        if (version == 1)
        {
            if (data.Length < 24 + 2) return null;
            path = Encoding.Unicode.GetString(data[24..Math.Min(data.Length, 24 + 520)]);
        }
        else if (version == 2)
        {
            if (data.Length < 28) return null;
            var chars = BitConverter.ToInt32(data[24..28]);
            if (chars <= 0 || 28 + chars * 2 > data.Length) return null;
            path = Encoding.Unicode.GetString(data.Slice(28, chars * 2));
        }
        else
        {
            return null;
        }

        var nul = path.IndexOf('\0');
        if (nul >= 0) path = path[..nul];
        return path.Length == 0 ? null : (path, deleted, size);
    }

    public static IReadOnlyList<RecycledItem> List(string recycleFolder)
    {
        var items = new List<RecycledItem>();
        if (!Directory.Exists(recycleFolder)) return items;

        foreach (var index in Directory.EnumerateFiles(recycleFolder, "$I*"))
        {
            try
            {
                if (ParseIndex(File.ReadAllBytes(index)) is not { } info) continue;
                var data = Path.Combine(recycleFolder, "$R" + Path.GetFileName(index)[2..]);
                if (File.Exists(data) || Directory.Exists(data))
                    items.Add(new RecycledItem(info.OriginalPath, info.DeletedUtc, info.Size, index, data));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return items;
    }

    /// <summary>
    /// 원래 경로들을 복원한다 (같은 경로가 여러 번 지워졌으면 가장 최근 것).
    /// 원래 위치에 이미 같은 이름이 있으면 덮어쓰지 않고 건너뛴다. 복원한 경로 목록을 돌려준다.
    /// </summary>
    public static IReadOnlyList<string> Restore(IEnumerable<string> originalPaths, Func<string, string?> recycleFolderForPath)
    {
        var restored = new List<string>();
        var cache = new Dictionary<string, IReadOnlyList<RecycledItem>>(StringComparer.OrdinalIgnoreCase);

        foreach (var original in originalPaths)
        {
            var folder = recycleFolderForPath(original);
            if (folder is null) continue;
            if (!cache.TryGetValue(folder, out var items)) cache[folder] = items = List(folder);

            var item = items
                .Where(i => string.Equals(i.OriginalPath, original, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => i.DeletedUtc)
                .FirstOrDefault();
            if (item is null || File.Exists(original) || Directory.Exists(original)) continue;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                if (Directory.Exists(item.DataPath)) Directory.Move(item.DataPath, original);
                else File.Move(item.DataPath, original);
                File.Delete(item.IndexFile);
                restored.Add(original);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return restored;
    }
}

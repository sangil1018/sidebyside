namespace SideBySide.Core.Sequences;

/// <summary>한 프레임: 번호와 전체 경로.</summary>
public readonly record struct SequenceFrame(int Number, string Path);

/// <summary>한 폴더 안에서 같은 접두어·구분자·확장자를 가진 번호 붙은 파일 묶음.</summary>
public sealed class ImageSequence
{
    public required string Directory { get; init; }

    public required string Prefix { get; init; }

    public required string Separator { get; init; }

    public required string Extension { get; init; }

    /// <summary>가장 많이 쓰인 자릿수.</summary>
    public required int Padding { get; init; }

    /// <summary>프레임 번호 오름차순.</summary>
    public required IReadOnlyList<SequenceFrame> Frames { get; init; }

    public int FirstFrame => Frames[0].Number;

    public int LastFrame => Frames[^1].Number;

    /// <summary>화면 표시용 이름: <c>shot_####.exr</c>.</summary>
    public string DisplayName => string.Concat(Prefix, Separator, new string('#', Math.Max(1, Padding)), ".", Extension);

    /// <summary>첫~끝 사이에 빠진 프레임 수.</summary>
    public int MissingFrames => (int)Math.Max(0, (long)LastFrame - FirstFrame + 1 - Frames.Count);
}

/// <summary>
/// 이미지 시퀀스 찾기. 우클릭 메뉴 표시 판단(빠른 검사)과 시퀀스 뷰어·번호 다시 매기기가 함께 쓴다.
/// </summary>
public static class SequenceDetector
{
    /// <summary>프레임 번호 이름을 가진 파일 경로들을 폴더·패턴별로 묶는다. <paramref name="minFrames"/>보다 적은 묶음은 버린다.</summary>
    public static List<ImageSequence> Group(IEnumerable<string> paths, int minFrames = 2)
    {
        var groups = new Dictionary<(string Dir, string Key), List<(SequenceName Name, string Path)>>();
        foreach (var path in paths)
        {
            if (!SequenceName.TryParse(System.IO.Path.GetFileName(path), out var name)) continue;
            var dir = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
            var key = (dir.ToUpperInvariant(), name.GroupKey);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add((name, path));
        }

        var result = new List<ImageSequence>();
        foreach (var list in groups.Values)
        {
            if (list.Count < minFrames) continue;

            // 같은 번호가 패딩만 달리 여러 개 있으면(1.png, 01.png) 첫 것만 쓴다
            var frames = list
                .GroupBy(f => f.Name.Frame)
                .Select(g => g.First())
                .OrderBy(f => f.Name.Frame)
                .Select(f => new SequenceFrame(f.Name.Frame, f.Path))
                .ToList();
            if (frames.Count < minFrames) continue;

            var first = list[0].Name;
            result.Add(new ImageSequence
            {
                Directory = System.IO.Path.GetDirectoryName(list[0].Path) ?? string.Empty,
                Prefix = first.Prefix,
                Separator = first.Separator,
                Extension = first.Extension,
                Padding = list.GroupBy(f => f.Name.Padding).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key,
                Frames = frames,
            });
        }

        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// 폴더(와 <paramref name="maxDepth"/>단계 하위 폴더)의 시퀀스. 접근할 수 없는 폴더는 건너뛴다.
    /// <paramref name="extensions"/>가 있으면 그 확장자만 (점 없이, 대소문자 무시).
    /// </summary>
    public static List<ImageSequence> Scan(string folder, int maxDepth, IReadOnlySet<string>? extensions = null, int minFrames = 2, CancellationToken cancellationToken = default)
    {
        var files = new List<string>();
        var pending = new Stack<(string Dir, int Depth)>();
        pending.Push((folder, 0));
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, depth) = pending.Pop();
            try
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*", options))
                {
                    if (extensions is null || extensions.Contains(Ext(file))) files.Add(file);
                }

                if (depth < maxDepth)
                {
                    foreach (var sub in System.IO.Directory.EnumerateDirectories(dir, "*", options)) pending.Push((sub, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 사라졌거나 접근 불가한 폴더
            }
        }

        return Group(files, minFrames);
    }

    /// <summary>파일 이름이 프레임 번호 형식인지 (파일 시스템 접근 없음 — 우클릭 메뉴용).</summary>
    public static bool LooksLikeFrame(string path, IReadOnlySet<string>? extensions = null)
        => SequenceName.TryParse(System.IO.Path.GetFileName(path), out _) && (extensions is null || extensions.Contains(Ext(path)));

    /// <summary>
    /// 폴더 바로 아래에 같은 패턴 파일이 2개 이상 있는지. 큰 폴더에서도 메뉴가 느려지지 않게 앞의 <paramref name="scanLimit"/>개만 본다.
    /// </summary>
    public static bool FolderHasSequence(string folder, IReadOnlySet<string>? extensions = null, int scanLimit = 400)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
            foreach (var file in System.IO.Directory.EnumerateFiles(folder, "*", options))
            {
                if (++count > scanLimit) break;
                if (extensions is not null && !extensions.Contains(Ext(file))) continue;
                if (!SequenceName.TryParse(System.IO.Path.GetFileName(file), out var name)) continue;
                if (!seen.Add(name.GroupKey)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static string Ext(string path) => System.IO.Path.GetExtension(path).TrimStart('.');
}

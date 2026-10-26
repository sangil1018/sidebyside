using SideBySide.Core.Sequences;

namespace SideBySide.Modules.SeqViewer;

/// <summary>레이어 하나의 원본: 프레임 파일 목록(번호 순). 번호 없는 단일 이미지도 1프레임 레이어가 된다.</summary>
public sealed class LayerSource
{
    public required string Name { get; init; }

    public required string Directory { get; init; }

    /// <summary>프레임 파일 (0번부터 = 레이어 안의 순서).</summary>
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>파일 이름의 프레임 번호 (표시용, 단일 이미지는 null).</summary>
    public required IReadOnlyList<int>? Numbers { get; init; }

    public int FrameCount => Files.Count;

    public bool IsSingleImage => Files.Count == 1;

    /// <summary>폴더(와 하위 폴더)의 레이어들. 번호 붙은 파일은 시퀀스로 묶고, 나머지 이미지는 한 장짜리 레이어.</summary>
    public static List<LayerSource> Scan(string folder, int maxDepth, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var pending = new Stack<(string Dir, int Depth)>();
        pending.Push((folder, 0));
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden };
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, depth) = pending.Pop();
            try
            {
                files.AddRange(System.IO.Directory.EnumerateFiles(dir, "*", options).Where(ImageFormats.IsImage));
                if (depth < maxDepth)
                {
                    foreach (var sub in System.IO.Directory.EnumerateDirectories(dir, "*", options)) pending.Push((sub, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return FromFiles(files);
    }

    public static List<LayerSource> FromFiles(IReadOnlyCollection<string> files)
    {
        var result = new List<LayerSource>();
        var sequences = SequenceDetector.Group(files, minFrames: 1);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in sequences)
        {
            foreach (var f in s.Frames) used.Add(f.Path);
            result.Add(new LayerSource
            {
                Name = s.Frames.Count == 1 ? Path.GetFileName(s.Frames[0].Path) : s.DisplayName,
                Directory = s.Directory,
                Files = s.Frames.Select(f => f.Path).ToList(),
                Numbers = s.Frames.Count == 1 ? null : s.Frames.Select(f => f.Number).ToList(),
            });
        }

        foreach (var file in files.Where(f => !used.Contains(f)))
        {
            result.Add(new LayerSource
            {
                Name = Path.GetFileName(file),
                Directory = Path.GetDirectoryName(file) ?? string.Empty,
                Files = [file],
                Numbers = null,
            });
        }

        // 프레임이 많은 시퀀스를 먼저 (보통 주 레이어)
        result.Sort((a, b) => b.FrameCount != a.FrameCount ? b.FrameCount.CompareTo(a.FrameCount) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }
}

namespace SideBySide.Core.FileSystem;

/// <summary>Windows 파일 이름 규칙과 경로 유틸.</summary>
public static class PathNames
{
    /// <summary>Windows에서 이름에 쓸 수 없는 문자 (OS와 무관하게 Windows 기준으로 검사).</summary>
    private static readonly char[] InvalidNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*', '\0'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>이름이 유효하지 않으면 사유를, 유효하면 null을 돌려준다.</summary>
    public static string? Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "이름이 비어 있습니다.";
        if (name.IndexOfAny(InvalidNameChars) >= 0) return "다음 문자는 사용할 수 없습니다: \\ / : * ? \" < > |";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "이름은 점이나 공백으로 끝날 수 없습니다.";
        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(name))) return "예약된 이름입니다.";
        return null;
    }

    /// <summary>"새 폴더", "새 폴더 (2)" … 처럼 겹치지 않는 경로를 만든다.</summary>
    public static string MakeUnique(string directory, string baseName, string extension = "")
    {
        var candidate = Path.Combine(directory, baseName + extension);
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
            candidate = Path.Combine(directory, $"{baseName} ({n}){extension}");
        return candidate;
    }

    public static bool IsSameOrChildOf(string path, string parent)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return p.Equals(r, StringComparison.OrdinalIgnoreCase)
               || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

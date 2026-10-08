namespace SideBySide.Core.FileSystem;

/// <summary>디렉터리 열거 결과 한 항목. 불변.</summary>
public sealed record FileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime LastWriteTimeUtc,
    FileAttributes Attributes)
{
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;

    public bool IsSystem => (Attributes & FileAttributes.System) != 0;

    /// <summary>확장자(점 포함, 예: ".txt"). 폴더는 빈 문자열.</summary>
    public string Extension => IsDirectory ? string.Empty : Path.GetExtension(Name);

    public static FileEntry FromPath(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        var isDir = info is DirectoryInfo;
        return new FileEntry(
            info.Name,
            info.FullName,
            isDir,
            isDir ? 0 : ((FileInfo)info).Length,
            info.LastWriteTimeUtc,
            info.Attributes);
    }
}

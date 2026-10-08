namespace SideBySide.Shell.Abstractions;

/// <summary>탐색기와 호환되는 파일 클립보드 (FileDrop + Preferred DropEffect).</summary>
public interface IFileClipboard
{
    void SetFiles(IReadOnlyList<string> paths, bool cut);

    /// <summary>클립보드에 파일이 없으면 null.</summary>
    (IReadOnlyList<string> Paths, bool Cut)? GetFiles();

    void SetText(string text);

    void Clear();
}

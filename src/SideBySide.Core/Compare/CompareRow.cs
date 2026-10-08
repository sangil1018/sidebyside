using SideBySide.Core.FileSystem;

namespace SideBySide.Core.Compare;

public enum CompareState
{
    Same,
    Different,
    LeftOnly,
    RightOnly,

    /// <summary>폴더 상태 계산 중 (하위 비교가 백그라운드에서 진행).</summary>
    Pending,

    /// <summary>읽기 실패 등으로 판정 불가.</summary>
    Error,
}

public enum NewerSide
{
    None,
    Left,
    Right,
}

/// <summary>좌우 정렬된 한 행. 같은 이름 항목이 같은 행에 놓인다.</summary>
public sealed class CompareRow
{
    public CompareRow(string name, bool isDirectory, FileEntry? left, FileEntry? right)
    {
        Name = name;
        IsDirectory = isDirectory;
        Left = left;
        Right = right;
        State = left is null ? CompareState.RightOnly
              : right is null ? CompareState.LeftOnly
              : CompareState.Pending;
    }

    public string Name { get; }

    public bool IsDirectory { get; }

    public FileEntry? Left { get; }

    public FileEntry? Right { get; }

    public CompareState State { get; set; }

    public NewerSide Newer { get; set; }

    public bool IsOneSided => Left is null || Right is null;
}

namespace SideBySide.Core.Diff;

/// <summary>텍스트 비교 시 무시할 차이. 줄끝(CRLF/LF)은 항상 무시하고 머리글에 따로 표시한다.</summary>
public sealed record DiffOptions
{
    public bool IgnoreWhitespace { get; init; }

    public bool IgnoreCase { get; init; }
}

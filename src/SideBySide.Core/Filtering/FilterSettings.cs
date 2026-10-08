namespace SideBySide.Core.Filtering;

/// <summary>
/// 사용자가 편집하는 필터 정의 (설정 파일에 그대로 저장됨).
/// 패턴은 ';' 또는 줄바꿈으로 구분. 끝에 '\' 또는 '/'를 붙이면 폴더 전용 패턴.
/// 예: <c>*.tmp; *.log; bin\; obj\; .git\</c>
/// </summary>
public sealed record FilterSettings
{
    public string Name { get; init; } = string.Empty;

    /// <summary>숨길 항목 패턴 (파일·폴더 모두 적용).</summary>
    public string Exclude { get; init; } = string.Empty;

    /// <summary>비어 있지 않으면 일치하는 파일만 표시 (폴더는 탐색을 위해 항상 표시).</summary>
    public string Include { get; init; } = string.Empty;

    public bool IsEmpty => string.IsNullOrWhiteSpace(Exclude) && string.IsNullOrWhiteSpace(Include);

    public static readonly FilterSettings None = new();
}

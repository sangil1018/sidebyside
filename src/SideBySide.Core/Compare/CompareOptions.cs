namespace SideBySide.Core.Compare;

public enum CompareMethod
{
    /// <summary>크기 + 수정 시각 (빠름, 기본값).</summary>
    SizeAndDate,

    /// <summary>바이트 단위 내용 비교 (정확, 느림).</summary>
    Content,
}

public sealed record CompareOptions
{
    public CompareMethod Method { get; init; } = CompareMethod.SizeAndDate;

    /// <summary>수정 시각 허용 오차. FAT/exFAT/네트워크 드라이브는 2초 단위로 저장되므로 기본 2초.</summary>
    public double TimeToleranceSeconds { get; init; } = 2;

    /// <summary>정확히 1시간 차이는 같은 것으로 본다 (서머타임·시간대 차이).</summary>
    public bool IgnoreHourOffset { get; init; }

    /// <summary>하위 폴더 내용까지 재귀 비교해 폴더 상태를 결정.</summary>
    public bool CompareSubfolders { get; init; } = true;
}

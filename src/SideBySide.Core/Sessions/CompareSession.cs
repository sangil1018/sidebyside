using SideBySide.Core.Compare;
using SideBySide.Core.Diff;
using SideBySide.Core.Filtering;

namespace SideBySide.Core.Sessions;

/// <summary>저장된 비교 세션: 자주 비교하는 좌/우 폴더 쌍과 비교 조건.</summary>
public sealed record CompareSession
{
    public string Name { get; init; } = string.Empty;

    public string LeftPath { get; init; } = string.Empty;

    public string RightPath { get; init; } = string.Empty;

    public FilterSettings Filter { get; init; } = FilterSettings.None;

    public CompareOptions Compare { get; init; } = new();

    public DiffOptions Diff { get; init; } = new();

    public bool ShowDifferencesOnly { get; init; }
}

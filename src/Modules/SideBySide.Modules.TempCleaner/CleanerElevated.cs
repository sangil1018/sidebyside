using System.Text.Json;

namespace SideBySide.Modules.TempCleaner;

/// <summary>승격 작업 입력: 정리할 시스템 카테고리 ID와 기간.</summary>
public sealed class CleanRequest
{
    public List<string> Ids { get; set; } = [];

    public int MinAgeDays { get; set; }
}

/// <summary>승격 작업 결과: 카테고리 ID → 결과.</summary>
public sealed class CleanResponse
{
    public Dictionary<string, CleanResult> Results { get; set; } = [];

    public List<string> Rejected { get; set; } = [];
}

/// <summary>
/// 관리자 권한 프로세스에서 실행되는 정리. 경로는 받지 않고, 승격 쪽에서 다시 만든 카테고리 중
/// <see cref="CleanTarget.RequiresAdmin"/>인 것만 ID로 고른다 (사용자 영역 경로를 승격 권한으로 지우는 일이 없게).
/// </summary>
public static class CleanerElevated
{
    public const string Operation = "tempcleaner.clean";

    public static string Clean(string payload)
    {
        var request = JsonSerializer.Deserialize<CleanRequest>(payload) ?? new CleanRequest();
        var allowed = Cleaner.BuildTargets().Where(t => t.RequiresAdmin).ToDictionary(t => t.Id);
        var response = new CleanResponse();
        foreach (var id in request.Ids.Distinct(StringComparer.Ordinal).Take(16))
        {
            if (!allowed.TryGetValue(id, out var target))
            {
                response.Rejected.Add(id);
                continue;
            }

            response.Results[id] = Cleaner.Clean(target, Math.Clamp(request.MinAgeDays, 0, 3650));
        }

        return JsonSerializer.Serialize(response);
    }
}

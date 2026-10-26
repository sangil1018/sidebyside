using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.LanShare;

public sealed class ApplyRequest
{
    public int AdapterIndex { get; set; }

    public int Role { get; set; }

    public string Folder { get; set; } = LanShareCore.DefaultFolder;

    public SharePermission Permission { get; set; } = SharePermission.Change;
}

public sealed class ResetRequest
{
    public int AdapterIndex { get; set; }

    public SavedIpConfig? Previous { get; set; }
}

public sealed record StepResult(string Description, bool Succeeded, string Output);

public sealed class StepsResponse
{
    public List<StepResult> Steps { get; set; } = [];

    public string? Error { get; set; }
}

/// <summary>
/// 관리자 권한 프로세스에서 실행되는 LAN 공유 적용/해제. 입력은 모두 다시 검증한다:
/// 어댑터는 실제 이더넷 어댑터 인덱스, 역할은 1/2, IP는 IPv4 형식, 공유 폴더는 허용된 로컬 폴더이며 경로에 링크가 없어야 한다.
/// 명령은 셸 없이 System32의 netsh.exe/net.exe를 인자 목록으로 실행한다.
/// </summary>
public static class LanShareElevated
{
    public const string ApplyOperation = "lanshare.apply";
    public const string ResetOperation = "lanshare.reset";

    public static string Apply(string payload)
    {
        var request = JsonSerializer.Deserialize<ApplyRequest>(payload) ?? new ApplyRequest();
        var response = new StepsResponse();
        if (ValidateAdapter(request.AdapterIndex) is { } adapterError) return Fail(response, adapterError);
        if (request.Role is not (1 or 2)) return Fail(response, L.T("역할은 PC1 또는 PC2여야 합니다."));
        if (!Enum.IsDefined(request.Permission)) return Fail(response, L.T("권한 값이 올바르지 않습니다."));
        if (LanShareCore.ValidateShareFolder(request.Folder) is { } folderError) return Fail(response, folderError);

        var folder = Path.GetFullPath(request.Folder).TrimEnd('\\');

        // 만들기 전에도 확인: 상위 폴더가 링크면 만들기 자체가 다른 곳(시스템 폴더 등)에 생긴다
        if (HasLinkInPath(folder)) return Fail(response, L.T("공유 폴더 경로에 바로 가기 링크(정션)가 있어 공유할 수 없습니다."));
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(response, ex.Message);
        }

        // 폴더나 상위 폴더가 링크면 다른 곳(시스템 폴더 등)을 공유하게 될 수 있어 거부
        if (HasLinkInPath(folder)) return Fail(response, L.T("공유 폴더 경로에 바로 가기 링크(정션)가 있어 공유할 수 없습니다."));

        var existing = LanShareCore.ExistingSharePath();
        var steps = LanShareCore.BuildApply(request.AdapterIndex, request.Role, folder, request.Permission, LanShareCore.EveryoneName(),
            shareExists: existing is not null);
        RunSteps(steps, response);
        if (existing is not null && !string.Equals(existing.TrimEnd('\\'), folder, StringComparison.OrdinalIgnoreCase))
            response.Steps.Add(new StepResult(L.F("'{0}' 공유가 이미 {1}을(를) 가리키고 있어 그대로 두었습니다. 바꾸려면 먼저 해제하세요.", LanShareCore.ShareName, existing), false, string.Empty));
        return JsonSerializer.Serialize(response);
    }

    public static string Reset(string payload)
    {
        var request = JsonSerializer.Deserialize<ResetRequest>(payload) ?? new ResetRequest();
        var response = new StepsResponse();
        if (ValidateAdapter(request.AdapterIndex) is { } adapterError) return Fail(response, adapterError);
        var previous = request.Previous;
        if (previous is not null)
        {
            var addressesOk = (previous.Dhcp || (LanShareCore.IsIPv4(previous.Address) && LanShareCore.IsIPv4(previous.Mask)))
                              && (previous.Gateway is null || LanShareCore.IsIPv4(previous.Gateway))
                              && previous.Dns.Count <= 2 && previous.Dns.All(LanShareCore.IsIPv4);
            if (!addressesOk) previous = null; // 이상한 값이면 원본처럼 DHCP로
        }

        RunSteps(LanShareCore.BuildReset(request.AdapterIndex, previous), response);
        return JsonSerializer.Serialize(response);
    }

    private static string? ValidateAdapter(int index)
        => LanShareCore.Adapters().Any(a => a.IPv4Index == index) ? null : L.T("선택한 유선 어댑터를 찾을 수 없습니다.");

    private static string Fail(StepsResponse response, string error)
    {
        response.Error = error;
        return JsonSerializer.Serialize(response);
    }

    private static bool HasLinkInPath(string folder)
    {
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
        {
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        }

        return false;
    }

    private static void RunSteps(IEnumerable<CommandStep> steps, StepsResponse response)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        foreach (var step in steps)
        {
            var start = new ProcessStartInfo(step.Program)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = oem,
                StandardErrorEncoding = oem,
            };
            foreach (var arg in step.Arguments) start.ArgumentList.Add(arg);

            try
            {
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30_000))
                {
                    process.Kill(entireProcessTree: true);
                    response.Steps.Add(new StepResult(step.Description, false, L.T("시간 초과")));
                    continue;
                }

                var text = (output.Result + error.Result).Trim();
                var ok = process.ExitCode == 0;
                response.Steps.Add(new StepResult(step.Description, ok || step.IgnoreFailure, text.Length > 2000 ? text[..2000] : text));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                response.Steps.Add(new StepResult(step.Description, step.IgnoreFailure, ex.Message));
            }
        }
    }
}

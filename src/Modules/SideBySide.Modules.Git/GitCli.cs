using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.Git;

/// <summary>git 명령 하나의 결과.</summary>
internal sealed record GitResult(int ExitCode, string Output, string Error)
{
    /// <summary>git 실행 파일을 찾지 못했을 때의 종료 코드.</summary>
    public const int NotInstalled = -1000;

    public bool Ok => ExitCode == 0;

    public bool GitMissing => ExitCode == NotInstalled;

    /// <summary>사용자에게 보여 줄 한 덩어리 메시지 (오류 출력 우선).</summary>
    public string Message
    {
        get
        {
            var text = Error.Trim();
            if (text.Length == 0) text = Output.Trim();
            return text.Length == 0 ? L.F("git이 종료 코드 {0}(으)로 끝났습니다.", ExitCode) : text;
        }
    }
}

/// <summary>
/// git.exe 실행기. 사용자의 git 설정·자격 증명 관리자·SSH 키를 그대로 쓰도록 시스템에 설치된 git을 부른다.
/// 콘솔 입력을 기다리며 멈추지 않게 터미널 프롬프트와 편집기를 끈다 (자격 증명 관리자의 창은 그대로 뜬다).
/// </summary>
internal static class GitCli
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <param name="stdin">표준 입력으로 넘길 내용 (경로 목록 등). null이면 입력 없음.</param>
    public static async Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default, string? stdin = null)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        if (stdin is not null) info.StandardInputEncoding = Utf8;

        // 출력 형식을 사용자 설정과 무관하게 고정
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("core.quotepath=false");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("color.ui=false");
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        info.Environment["GIT_TERMINAL_PROMPT"] = "0"; // 콘솔에서 아이디·암호를 묻지 않는다
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";  // 상태 조회가 index를 잠그지 않게 (다른 git 작업과 충돌 방지)
        info.Environment["GIT_EDITOR"] = "true";       // 병합 메시지 편집기를 띄우지 않는다
        info.Environment["GIT_MERGE_AUTOEDIT"] = "no";
        info.Environment["GIT_PAGER"] = "cat";

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) return Missing();
        }
        catch (Win32Exception)
        {
            return Missing();
        }

        try
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new GitResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        catch (IOException ex)
        {
            Kill(process);
            return new GitResult(-1, string.Empty, ex.Message);
        }
    }

    private static GitResult Missing()
        => new(GitResult.NotInstalled, string.Empty,
            L.T("git을 찾을 수 없습니다. Git for Windows를 설치한 뒤 다시 시도하세요."));

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // 이미 끝났거나 끝낼 수 없는 프로세스
        }
    }
}

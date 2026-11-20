using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.Svn;

/// <summary>svn 명령 하나의 결과.</summary>
internal sealed record SvnResult(int ExitCode, string Output, string Error)
{
    /// <summary>svn 실행 파일을 찾지 못했을 때의 종료 코드.</summary>
    public const int NotInstalled = -1000;

    public bool Ok => ExitCode == 0;

    public bool SvnMissing => ExitCode == NotInstalled;

    /// <summary>사용자에게 보여 줄 한 덩어리 메시지 (오류 출력 우선).</summary>
    public string Message
    {
        get
        {
            var text = Error.Trim();
            if (text.Length == 0) text = Output.Trim();
            return text.Length == 0 ? L.F("svn이 종료 코드 {0}(으)로 끝났습니다.", ExitCode) : text;
        }
    }
}

/// <summary>
/// svn.exe 실행기. TortoiseSVN(명령줄 도구 포함)·SlikSVN 등 시스템에 설치된 svn을 불러
/// 저장된 자격 증명과 설정을 그대로 쓴다. 콘솔 입력을 기다리며 멈추지 않게 항상 <c>--non-interactive</c>로 실행한다.
/// </summary>
internal static class SvnCli
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Lazy<string?> Executable = new(FindExecutable);
    private static readonly Lazy<Encoding> Native = new(NativeEncoding);

    /// <summary>svn이 메시지·목록을 내보내는 인코딩 (Windows의 ANSI 코드 페이지).</summary>
    public static Encoding NativeOutput => Native.Value;

    /// <param name="arguments">첫 항목은 하위 명령 (status, diff …).</param>
    /// <param name="outputEncoding">표준 출력 인코딩. 기본은 UTF-8 (<c>--xml</c> 출력과 파일 내용).</param>
    public static async Task<SvnResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default, Encoding? outputEncoding = null)
    {
        if (Executable.Value is not { } exe) return Missing();

        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding ?? Utf8,
            StandardErrorEncoding = NativeOutput,
        };

        for (int i = 0; i < arguments.Count; i++)
        {
            info.ArgumentList.Add(arguments[i]);
            if (i == 0) info.ArgumentList.Add("--non-interactive"); // 아이디·암호·인증서 확인을 콘솔에서 묻지 않는다
        }

        info.Environment["SVN_EDITOR"] = "rem"; // 혹시 편집기를 부르더라도 창을 띄우지 않는다

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
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new SvnResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        catch (IOException ex)
        {
            Kill(process);
            return new SvnResult(-1, string.Empty, ex.Message);
        }
    }

    private static SvnResult Missing()
        => new(SvnResult.NotInstalled, string.Empty,
            L.T("svn을 찾을 수 없습니다. TortoiseSVN(설치할 때 '명령줄 도구' 포함) 또는 SlikSVN을 설치한 뒤 다시 시도하세요."));

    /// <summary>PATH에서 찾고, 없으면 TortoiseSVN·SlikSVN의 기본 설치 위치를 본다 (TortoiseSVN은 PATH에 넣지 않는 경우가 많다).</summary>
    private static string? FindExecutable()
    {
        var candidates = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(dir.Trim().Trim('"'), "svn.exe"));
            }
            catch (ArgumentException)
            {
                // 잘못된 PATH 항목
            }
        }

        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var programFiles = Environment.GetFolderPath(root);
            if (programFiles.Length == 0) continue;
            candidates.Add(Path.Combine(programFiles, "TortoiseSVN", "bin", "svn.exe"));
            candidates.Add(Path.Combine(programFiles, "SlikSvn", "bin", "svn.exe"));
            candidates.Add(Path.Combine(programFiles, "VisualSVN", "bin", "svn.exe"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static Encoding NativeEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Utf8;
        }
    }

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

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SideBySide.App.Infrastructure;
using SideBySide.Core.Localization;
using SideBySide.Core.Settings;
using SideBySide.Modules;

namespace SideBySide.App.Modules;

/// <summary>
/// 관리자 권한이 필요한 모듈 작업을 위한 1회용 승격 프로세스.
/// <list type="bullet">
/// <item>앱(일반 권한)이 이름 없는 GUID 파이프를 현재 사용자 전용·인스턴스 1개로 먼저 만들고, 같은 exe를 <c>--elevated &lt;작업&gt; &lt;파이프&gt; &lt;SHA-256&gt;</c>로 UAC 실행한다.</item>
/// <item>승격 프로세스는 파이프로 입력을 받아 명령줄의 해시와 대조한 뒤, <see cref="ModuleCatalog"/>에 등록된 작업만 실행하고 결과를 파이프로 돌려준다.</item>
/// <item>승격 프로세스는 파일을 만들거나 쓰지 않는다 (사용자 쓰기 가능 경로를 통한 바꿔치기·링크 공격 차단). 각 작업은 입력을 다시 검증한다.</item>
/// </list>
/// </summary>
public static partial class Elevation
{
    public const string Switch = "--elevated";
    private const int MaxMessage = 1024 * 1024;
    private const byte StatusOk = 0;
    private const byte StatusError = 1;

    [GeneratedRegex("^[a-z]+\\.[a-z]+$")]
    private static partial Regex OperationPattern();

    [GeneratedRegex("^SideBySide-elev-[0-9a-f]{32}$")]
    private static partial Regex PipePattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();

    // ── 앱 쪽 ─────────────────────────────────────────────────────

    /// <summary>작업을 승격 실행한다. UAC를 거부하면 <see cref="ElevatedResult.Cancelled"/>.</summary>
    public static async Task<ElevatedResult> RunAsync(string operation, string payload, CancellationToken cancellationToken)
    {
        if (!OperationPattern().IsMatch(operation) || ModuleCatalog.FindElevated(operation) is null)
            return new ElevatedResult(false, null, L.F("허용되지 않은 작업입니다: {0}", operation));
        if (Environment.ProcessPath is not { } exe) return new ElevatedResult(false, null, L.T("실행 파일 위치를 알 수 없습니다."));

        var bytes = Encoding.UTF8.GetBytes(payload);
        if (bytes.Length > MaxMessage) return new ElevatedResult(false, null, L.T("작업 입력이 너무 큽니다."));

        var pipeName = "SideBySide-elev-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        Process? process;
        try
        {
            // UAC 확인 창이 떠 있는 동안 Process.Start가 반환하지 않으므로 UI 스레드를 막지 않게 작업자에서 시작
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"{Switch} {operation} {pipeName} {Hash(bytes)}",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            process = await Task.Run(() => Process.Start(start), cancellationToken);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ElevatedResult(true, null, null); // ERROR_CANCELLED: UAC 거부
        }
        catch (Win32Exception ex)
        {
            return new ElevatedResult(false, null, ex.Message);
        }

        if (process is null) return new ElevatedResult(false, null, L.T("관리자 권한 작업을 시작하지 못했습니다."));

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var connect = pipe.WaitForConnectionAsync(timeout.Token);
                var exited = process.WaitForExitAsync(timeout.Token);
                if (await Task.WhenAny(connect, exited) != connect || !pipe.IsConnected)
                    return new ElevatedResult(false, null, L.T("관리자 권한 작업이 응답하지 않았습니다."));

                await WriteMessageAsync(pipe, StatusOk, bytes, cancellationToken);
                var (status, result) = await ReadMessageAsync(pipe, cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                var text = Encoding.UTF8.GetString(result);
                return status == StatusOk ? new ElevatedResult(false, text, null) : new ElevatedResult(false, null, text);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or EndOfStreamException)
            {
                return new ElevatedResult(false, null, cancellationToken.IsCancellationRequested ? L.T("취소했습니다.") : L.T("관리자 권한 작업과의 연결이 끊겼습니다."));
            }
        }
    }

    // ── 승격 프로세스 쪽 ─────────────────────────────────────────

    /// <summary><c>--elevated</c>로 시작했으면 작업을 처리하고 종료 코드를 돌려준다. 아니면 null.</summary>
    public static int? TryRunElevated(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != Switch) return null;
        if (args.Count != 4 || !OperationPattern().IsMatch(args[1]) || !PipePattern().IsMatch(args[2]) || !HashPattern().IsMatch(args[3])) return 2;
        if (ModuleCatalog.FindElevated(args[1]) is not { } handler) return 2;

        try
        {
            // 메시지 언어만 사용자 설정을 따른다. 읽기만 한다 (설정 저장소의 손상 백업 같은 쓰기를 승격 권한으로 하지 않게)
            UseLanguageReadOnly();

            using var pipe = new NamedPipeClientStream(".", args[2], PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(10_000);
            var (_, payload) = ReadMessageAsync(pipe, CancellationToken.None).GetAwaiter().GetResult();
            if (!string.Equals(Hash(payload), args[3], StringComparison.Ordinal)) return 3; // 입력이 바뀌었다

            byte status;
            string result;
            try
            {
                result = handler(Encoding.UTF8.GetString(payload));
                status = StatusOk;
            }
            catch (Exception ex)
            {
                result = ex.Message;
                status = StatusError;
            }

            WriteMessageAsync(pipe, status, Encoding.UTF8.GetBytes(result), CancellationToken.None).GetAwaiter().GetResult();
            pipe.WaitForPipeDrain();
            return status == StatusOk ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidDataException)
        {
            return 4; // 승격 프로세스는 오류 로그 파일도 쓰지 않는다
        }
    }

    private static void UseLanguageReadOnly()
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(JsonSettingsStore.DefaultPath));
            if (doc.RootElement.TryGetProperty("Language", out var language) && language.GetString() == nameof(AppLanguage.English))
                L.Use(AppLanguage.English);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
        }
    }

    internal static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static async Task WriteMessageAsync(Stream stream, byte status, byte[] body, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        header[0] = status;
        BitConverter.TryWriteBytes(header.AsSpan(1), body.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<(byte Status, byte[] Body)> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BitConverter.ToInt32(header, 1);
        if (length < 0 || length > MaxMessage) throw new InvalidDataException("message too large");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return (header[0], body);
    }
}

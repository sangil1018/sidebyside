using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using SideBySide.Core;

namespace SideBySide.App.Infrastructure;

/// <summary>
/// 멈춤·느려짐 진단 기록 (<see cref="AppPaths.DiagVariable"/>=1일 때만, 위치는 <see cref="AppPaths.DiagLog"/>).
/// <list type="bullet">
/// <item>UI 스레드 감시: 250ms마다 입력과 같은 우선순위로 빈 작업을 넣어, 처리될 때까지 걸린 시간이 길면 기록한다
/// (화면은 떠 있지만 클릭·키 입력이 먹히지 않는 상태를 잡는다).</item>
/// <item>호출 계수: <see cref="Hit"/>를 1초 단위로 모아 기록하고, 초당 <see cref="LoopWarning"/>회를 넘는 반복은 경고로 표시한다.</item>
/// </list>
/// 꺼져 있으면 <see cref="Hit"/>는 bool 검사 한 번으로 끝난다.
/// </summary>
internal static class Diag
{
    /// <summary>초당 이 횟수를 넘게 불리면 끝없는 반복으로 의심한다.</summary>
    public const int LoopWarning = 200;

    private const int SlowUiMs = 200;
    private const long MaxBytes = 2 * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, int> Counts = new(StringComparer.Ordinal);
    private static readonly object WriteLock = new();
    private static Timer? _flushTimer;
    private static Thread? _watchdog;

    public static bool IsOn { get; } = AppPaths.IsDiagnostics;

    /// <summary>호출 한 번을 센다 (진단이 꺼져 있으면 아무것도 하지 않는다).</summary>
    public static void Hit(string name)
    {
        if (IsOn) Counts.AddOrUpdate(name, 1, static (_, v) => v + 1);
    }

    /// <summary>앱 시작 시 한 번: 감시 스레드와 기록 타이머를 시작한다.</summary>
    public static void Start(Dispatcher ui)
    {
        if (!IsOn || _watchdog is not null) return;
        Write($"── 진단 시작 (pid {Environment.ProcessId}, 자동화 {AutomationMode.IsOn}) ──");
        _flushTimer = new Timer(_ => Flush(), null, 1000, 1000);
        _watchdog = new Thread(() => WatchUi(ui)) { IsBackground = true, Name = "SideBySide.Diag.Watchdog" };
        _watchdog.Start();
    }

    /// <summary>자유 형식 한 줄 (드라이버·하네스가 단계 표시용으로 쓴다).</summary>
    public static void Note(string text)
    {
        if (IsOn) Write(text);
    }

    private static void WatchUi(Dispatcher ui)
    {
        var watch = new Stopwatch();
        while (!ui.HasShutdownStarted)
        {
            watch.Restart();
            DispatcherOperation op;
            try
            {
                op = ui.InvokeAsync(static () => { }, DispatcherPriority.Input);
            }
            catch (InvalidOperationException)
            {
                return; // 디스패처 종료
            }

            var reported = false;
            while (!op.Task.Wait(5000))
            {
                if (ui.HasShutdownStarted) return;
                if (!reported) Write($"[ui] 응답 없음: 입력 처리가 {watch.ElapsedMilliseconds:N0}ms째 밀려 있음");
                reported = true;
            }

            var ms = watch.ElapsedMilliseconds;
            if (ms >= SlowUiMs) Write($"[ui] 지연 {ms:N0}ms" + (reported ? " (풀림)" : string.Empty));
            Thread.Sleep(250);
        }
    }

    private static void Flush()
    {
        if (Counts.IsEmpty) return;
        var snapshot = new List<KeyValuePair<string, int>>();
        foreach (var key in Counts.Keys)
        {
            if (Counts.TryRemove(key, out var count)) snapshot.Add(new(key, count));
        }

        if (snapshot.Count == 0) return;
        snapshot.Sort((a, b) => b.Value.CompareTo(a.Value));
        var loops = snapshot.Where(p => p.Value > LoopWarning).Select(p => p.Key).ToList();
        var line = "[calls] " + string.Join(", ", snapshot.Select(p => p.Key + "=" + p.Value.ToString(CultureInfo.InvariantCulture)));
        if (loops.Count > 0) line += "  ⚠ 반복 의심: " + string.Join(", ", loops);
        Write(line);
    }

    private static void Write(string text)
    {
        try
        {
            lock (WriteLock)
            {
                var path = AppPaths.DiagLog;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + text + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 진단 기록 실패는 무시
        }
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using SideBySide.UiDriver;

// 사용법:
//   SideBySide.UiDriver inspect <SideBySide.exe> [--depth N]
//   SideBySide.UiDriver run <SideBySide.exe> --scenario smoke|compare-copy
// 앱은 임시 데이터 폴더·자동화 모드(화면 밖, 포커스 없음, 전역 단축키 없음)·진단 기록으로 뜬다.
// 조작은 UI Automation 패턴만 쓰므로 사용자의 마우스·키보드·포커스를 건드리지 않는다.

Console.OutputEncoding = Encoding.UTF8;
if (args.Length < 2 || args[0] is not ("inspect" or "run"))
{
    Console.WriteLine("사용법: SideBySide.UiDriver inspect <exe> [--depth N] | run <exe> --scenario smoke|compare-copy");
    return 2;
}

var exe = args[1];
if (!File.Exists(exe))
{
    Console.WriteLine($"실행 파일이 없습니다: {exe}");
    return 2;
}

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

return args[0] == "inspect" ? Inspect(exe, int.TryParse(Option("--depth"), out var depth) ? depth : 6) : Run(exe, Option("--scenario") ?? "smoke");

static int Inspect(string exe, int depth)
{
    using var app = AppSession.Launch(exe, null, []);
    Thread.Sleep(1500);
    Print(app.Window, 0, depth);
    return 0;

    static void Print(AutomationElement element, int level, int depth)
    {
        var c = element.Current;
        var id = c.AutomationId.Length > 0 ? $" #{c.AutomationId}" : string.Empty;
        var name = c.Name.Length > 0 ? $" \"{(c.Name.Length > 60 ? c.Name[..60] + "…" : c.Name)}\"" : string.Empty;
        Console.WriteLine($"{new string(' ', level * 2)}{c.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty)}{id}{name}");
        if (level >= depth) return;
        var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
        while (child is not null)
        {
            Print(child, level + 1, depth);
            child = TreeWalker.ControlViewWalker.GetNextSibling(child);
        }
    }
}

static int Run(string exe, string scenario)
{
    var fixture = Path.Combine(Path.GetTempPath(), "sidebyside-uidriver-files-" + Guid.NewGuid().ToString("N"));
    var report = new Report();
    try
    {
        switch (scenario)
        {
            case "smoke":
                Smoke(exe, fixture, report);
                break;
            case "compare-copy":
                CompareCopy(exe, fixture, report);
                break;
            default:
                Console.WriteLine($"알 수 없는 시나리오: {scenario}");
                return 2;
        }
    }
    catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ElementNotAvailableException or ElementNotEnabledException)
    {
        report.Fail(ex.Message);
    }
    finally
    {
        TryDelete(fixture);
    }

    return report.Print();
}

// 한 창 → 두 창 → 주소 이동 → 창 바꾸기 → 비교 켜고 끄기 → 한 창
static void Smoke(string exe, string fixture, Report report)
{
    var a = MakeDir(fixture, "a");
    var b = MakeDir(fixture, "b");
    File.WriteAllText(Path.Combine(a, "only-a.txt"), "a");
    File.WriteAllText(Path.Combine(b, "only-b.txt"), "b");

    using var app = AppSession.Launch(exe, Settings(dual: false, compare: false, left: a, right: null), []);
    report.Session = app;
    report.Step("시작 (한 창)", () => Wait.Until(() => app.Items("left").Contains("only-a.txt"), StepTimeout, "왼쪽 목록"));
    report.Step("두 창으로", () =>
    {
        app.Choose("Mode.Dual");
        _ = app.Pane("right"); // 오른쪽 창이 화면 트리에 나타날 때까지 기다린다
    });
    report.Step("오른쪽 주소 이동", () =>
    {
        app.Navigate("right", b);
        Wait.Until(() => app.Items("right").Contains("only-b.txt"), StepTimeout, "오른쪽 목록");
    });
    report.Step("좌우 창 바꾸기", () =>
    {
        app.Invoke("cmd.SwapPanes");
        Wait.Until(() => Same(app.Address("left"), b) && Same(app.Address("right"), a), StepTimeout, "바뀐 주소");
    });
    report.Step("비교 켜기", () =>
    {
        app.SetToggle("cmd.ToggleCompare", true);
        Wait.Until(() => app.Items("left").Contains("only-b.txt") && app.Items("right").Contains("only-a.txt")
                         && app.Items("left").Count == app.Items("right").Count, StepTimeout, "비교 행 정렬");
    });
    report.Step("비교 끄기 · 한 창으로", () =>
    {
        app.SetToggle("cmd.ToggleCompare", false);
        app.Choose("Mode.Single");
        Wait.Until(() => !app.HasExited, StepTimeout, "앱 실행 중");
    });
    report.Diag(app);
}

// 비교 모드('차이만')에서 오른쪽 폴더 전체 선택 → 왼쪽으로 복사 → 화면이 계속 입력을 받는지 (v1.7.1 멈춤 회귀)
static void CompareCopy(string exe, string fixture, Report report)
{
    var left = MakeDir(fixture, "left");
    var right = MakeDir(fixture, "right");
    File.WriteAllText(Path.Combine(left, "readme.txt"), "left only");
    for (int d = 0; d < 6; d++)
    {
        var dir = MakeDir(right, Path.Combine("ControlRoom", $"sub{d}", "deep"));
        for (int f = 0; f < 15; f++) File.WriteAllText(Path.Combine(dir, $"f{f}.cs"), "// code\n");
    }

    File.WriteAllText(Path.Combine(right, "ControlRoom.meta"), "meta");

    using var app = AppSession.Launch(exe, Settings(dual: true, compare: true, left: left, right: right), []);
    report.Session = app;
    report.Step("첫 비교", () => Wait.Until(() => app.Items("right").Contains("ControlRoom"), StepTimeout, "오른쪽 ControlRoom"));
    report.Step("오른쪽 전체 선택 → 왼쪽으로 복사", () =>
    {
        app.SelectAll("right");
        app.Invoke("cmd.CopyToLeft");
        Wait.Until(() => Directory.Exists(Path.Combine(left, "ControlRoom")) && File.Exists(Path.Combine(left, "ControlRoom.meta")), StepTimeout, "복사 완료");
    });
    report.Step("복사 뒤 3초간 응답 유지", () =>
    {
        Thread.Sleep(3000);
        var problems = app.DiagProblems();
        if (problems.Count > 0) throw new InvalidOperationException("UI 지연·반복: " + problems[0]);
    });
    report.Step("비교 결과 정리 ('차이만'에서 같아진 ControlRoom이 빠짐)", () =>
        Wait.Until(() => !app.Items("left").Contains("ControlRoom") && app.Items("left").Count == app.Items("right").Count, StepTimeout, "행 정리"));
    report.Diag(app);
}

static JsonObject Settings(bool dual, bool compare, string left, string? right)
{
    var settings = new JsonObject
    {
        ["Theme"] = "Light",
        ["DualPane"] = dual,
        ["CompareMode"] = compare,
        ["ShowDifferencesOnly"] = compare,
        ["Left"] = new JsonObject { ["Tabs"] = new JsonArray(left), ["ActiveTab"] = 0 },
    };
    if (right is not null) settings["Right"] = new JsonObject { ["Tabs"] = new JsonArray(right), ["ActiveTab"] = 0 };
    return settings;
}

static string MakeDir(string root, string relative) => Directory.CreateDirectory(Path.Combine(root, relative)).FullName;

static bool Same(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

static void TryDelete(string path)
{
    try
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // 임시 폴더 정리 실패는 결과와 무관
    }
}

internal sealed partial class Program
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);
}

/// <summary>단계별 결과와 진단 요약.</summary>
internal sealed class Report
{
    private readonly List<(string Name, bool Ok, long Ms, string? Error)> _steps = [];
    private readonly List<string> _diag = [];
    private bool _stopped;

    /// <summary>단계마다 앱이 전경 창을 가져갔는지 확인할 대상 (사용자 작업 방해 여부).</summary>
    public AppSession? Session { get; set; }

    public void Step(string name, Action action)
    {
        if (_stopped) return;
        var watch = Stopwatch.StartNew();
        try
        {
            action();
            Thread.Sleep(150); // 활성화 메시지가 처리될 시간
            if (Session?.IsForeground() == true)
                throw new InvalidOperationException("앱이 전경 창을 가져갔습니다 (사용자 입력을 가로챌 수 있음).");
            _steps.Add((name, true, watch.ElapsedMilliseconds, null));
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ElementNotAvailableException or ElementNotEnabledException)
        {
            _steps.Add((name, false, watch.ElapsedMilliseconds, ex.Message));
            _stopped = true;
        }
    }

    public void Fail(string message) => _steps.Add(("실행", false, 0, message));

    public void Diag(AppSession app)
    {
        _diag.Add("데이터 폴더(사용자 설정과 분리): " + app.DataDir);
        _diag.AddRange(app.DiagProblems().Select(p => "⚠ " + p));
    }

    public int Print()
    {
        foreach (var (name, ok, ms, error) in _steps)
            Console.WriteLine($"{(ok ? "✓" : "✗")} {name} ({ms:N0}ms){(error is null ? string.Empty : " — " + error)}");
        foreach (var line in _diag) Console.WriteLine(line);
        var failed = _steps.Any(s => !s.Ok) || _diag.Any(l => l.StartsWith('⚠'));
        Console.WriteLine(failed ? "결과: 실패" : "결과: 통과");
        return failed ? 1 : 0;
    }
}

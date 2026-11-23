using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace SideBySide.UiDriver;

/// <summary>
/// 분리된 SideBySide 인스턴스 하나: 임시 데이터 폴더에 시작 설정을 써 두고 자동화·진단 모드로 띄운다.
/// 창은 화면 밖에 포커스 없이 뜨고(앱의 자동화 모드), 이 클래스는 UI Automation 패턴으로만 조작한다.
/// </summary>
internal sealed class AppSession : IDisposable
{
    private readonly Process _process;

    private AppSession(Process process, string dataDir, AutomationElement window)
    {
        _process = process;
        DataDir = dataDir;
        Window = window;
    }

    public string DataDir { get; }

    public AutomationElement Window { get; }

    public string DiagLog => Path.Combine(DataDir, "diag.log");

    /// <param name="settings">시작 설정 (settings.json에 그대로 쓴다). null이면 기본값.</param>
    public static AppSession Launch(string exe, JsonObject? settings, IReadOnlyList<string> args)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "sidebyside-uidriver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        if (settings is not null) File.WriteAllText(Path.Combine(dataDir, "settings.json"), settings.ToJsonString());

        var info = new ProcessStartInfo(Path.GetFullPath(exe)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe))! };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["SIDEBYSIDE_DATA_DIR"] = dataDir;
        info.Environment["SIDEBYSIDE_AUTOMATION"] = "1";
        info.Environment["SIDEBYSIDE_DIAG"] = "1";

        var process = Process.Start(info) ?? throw new InvalidOperationException("앱을 시작하지 못했습니다.");
        try
        {
            var window = Wait.Until(() => FindMainWindow(process.Id), TimeSpan.FromSeconds(30), "메인 창");
            return new AppSession(process, dataDir, window);
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    public bool HasExited => _process.HasExited;

    /// <summary>앱이 전경 창(사용자 입력을 받는 창)을 차지하고 있는지 — 자동화 인스턴스는 절대 그러면 안 된다.</summary>
    public bool IsForeground()
    {
        _ = GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid == (uint)_process.Id;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    // ── 찾기 ────────────────────────────────────────────────────────

    public AutomationElement ById(string automationId, AutomationElement? under = null, double seconds = 10)
        => Wait.Until(() => (under ?? Window).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId)),
            TimeSpan.FromSeconds(seconds), "요소 " + automationId);

    /// <summary>팝업(도구 메뉴 등)은 메인 창 밖의 별도 창이라 같은 프로세스의 최상위 창 전체에서 찾는다.</summary>
    public AutomationElement ByIdAnywhere(string automationId, double seconds = 10)
        => Wait.Until(() =>
        {
            var condition = new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, _process.Id),
                new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
            return AutomationElement.RootElement.FindFirst(TreeScope.Descendants, condition);
        }, TimeSpan.FromSeconds(seconds), "요소 " + automationId);

    /// <summary>목록 항목: 자세히 보기(GridView)에서는 DataItem, 아이콘 보기에서는 ListItem.</summary>
    private static readonly Condition ItemCondition = new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));

    public AutomationElement Pane(string side) => ById(side == "left" ? "LeftPane" : "RightPane");

    public AutomationElement FileList(string side) => ById("FileList", Pane(side));

    /// <summary>파일 목록 항목 이름 (화면에 만들어진 항목만 — 작은 폴더 기준).</summary>
    public IReadOnlyList<string> Items(string side)
        => FileList(side).FindAll(TreeScope.Children, ItemCondition)
            .Cast<AutomationElement>().Select(e => e.Current.Name).ToList();

    // ── 조작 (모두 UI Automation 패턴, 마우스·키보드 없음) ───────────

    public void Invoke(string automationId) => ((InvokePattern)ById(automationId).GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    public void InvokeAnywhere(string automationId) => ((InvokePattern)ByIdAnywhere(automationId).GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    /// <summary>라디오 단추(한 창/두 창) 선택.</summary>
    public void Choose(string automationId) => ((SelectionItemPattern)ById(automationId).GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

    /// <summary>토글(비교 등)을 원하는 상태로.</summary>
    public void SetToggle(string automationId, bool on)
    {
        var element = ById(automationId);
        var toggle = (TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern);
        if ((toggle.Current.ToggleState == ToggleState.On) != on)
        {
            // 명령이 붙은 토글은 Invoke가 명령을 실행한다
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
            else toggle.Toggle();
        }
    }

    /// <summary>주소 표시줄에 경로를 넣어 이동 (앱이 주소 표시줄을 값 패턴으로 노출한다).</summary>
    public void Navigate(string side, string path)
        => ((ValuePattern)ById("Address", Pane(side)).GetCurrentPattern(ValuePattern.Pattern)).SetValue(path);

    public string Address(string side) => ((ValuePattern)ById("Address", Pane(side)).GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

    /// <summary>목록 항목을 이름으로 골라 선택 (첫 항목은 선택을 바꾸고, 나머지는 더한다).</summary>
    public void Select(string side, params string[] names)
    {
        var list = FileList(side);
        var first = true;
        foreach (var name in names)
        {
            var item = Wait.Until(() => list.FindFirst(TreeScope.Children, new AndCondition(
                    ItemCondition,
                    new PropertyCondition(AutomationElement.NameProperty, name))),
                TimeSpan.FromSeconds(10), $"{side} 항목 {name}");
            var pattern = (SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern);
            if (first) pattern.Select();
            else pattern.AddToSelection();
            first = false;
        }
    }

    /// <summary>목록 전체 선택 (빈 자리 표시 줄 포함 — 사용자의 Ctrl+A와 같다).</summary>
    public void SelectAll(string side)
    {
        var items = FileList(side).FindAll(TreeScope.Children, ItemCondition);
        for (int i = 0; i < items.Count; i++)
        {
            var pattern = (SelectionItemPattern)items[i].GetCurrentPattern(SelectionItemPattern.Pattern);
            if (i == 0) pattern.Select();
            else pattern.AddToSelection();
        }
    }

    // ── 진단 ────────────────────────────────────────────────────────

    /// <summary>진단 로그에서 UI 지연·반복 경고 줄.</summary>
    public IReadOnlyList<string> DiagProblems(int slowMs = 500)
    {
        if (!File.Exists(DiagLog)) return [];
        var problems = new List<string>();
        foreach (var line in File.ReadLines(DiagLog))
        {
            if (line.Contains("응답 없음", StringComparison.Ordinal) || line.Contains("반복 의심", StringComparison.Ordinal)) problems.Add(line);
            else if (line.Contains("[ui] 지연", StringComparison.Ordinal) && LatencyOf(line) >= slowMs) problems.Add(line);
        }

        return problems;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited && Window.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
                ((WindowPattern)pattern).Close();
            if (!_process.WaitForExit(10000)) Kill(_process);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            Kill(_process);
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static AutomationElement? FindMainWindow(int processId)
        => AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)));

    private static int LatencyOf(string line)
    {
        var start = line.IndexOf("지연 ", StringComparison.Ordinal) + 3;
        var end = line.IndexOf("ms", start, StringComparison.Ordinal);
        return start >= 3 && end > start && int.TryParse(line[start..end].Replace(",", string.Empty, StringComparison.Ordinal), out var ms) ? ms : 0;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 이미 끝남
        }
    }
}

/// <summary>조건이 이루어질 때까지 짧게 반복 확인.</summary>
internal static class Wait
{
    public static T Until<T>(Func<T?> probe, TimeSpan timeout, string what) where T : class
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (probe() is { } value) return value;
            }
            catch (ElementNotAvailableException)
            {
                // 화면이 바뀌는 중
            }

            if (watch.Elapsed > timeout) throw new TimeoutException($"{what}: {timeout.TotalSeconds:N0}초 안에 찾지 못했습니다.");
            Thread.Sleep(100);
        }
    }

    public static void Until(Func<bool> condition, TimeSpan timeout, string what)
        => Until(() => condition() ? "ok" : null, timeout, what);
}

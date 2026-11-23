using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SideBySide.App;
using SideBySide.App.Commands;
using SideBySide.App.Services;
using SideBySide.App.ViewModels;
using SideBySide.Core;
using SideBySide.Core.FileSystem;
using SideBySide.Core.Filtering;
using SideBySide.Core.Localization;
using SideBySide.Core.Rename;
using SideBySide.Core.Sessions;
using SideBySide.Core.Settings;
using Xunit.Sdk;

// WPF Application은 프로세스에 하나뿐이고 시나리오는 같은 UI 스레드를 쓰므로 하나씩 실행한다
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SideBySide.Scenarios.Tests;

/// <summary>
/// 모든 시나리오가 함께 쓰는 UI 스레드. 앱 리소스(<see cref="App.App"/>)를 한 번 올려 두고,
/// 시나리오를 그 스레드의 디스패처에서 실행한다 (await가 같은 스레드로 돌아온다).
/// </summary>
internal static class UiThread
{
    private static readonly Lazy<Dispatcher> Instance = new(Start);

    public static Dispatcher Dispatcher => Instance.Value;

    /// <summary>시나리오 하나를 UI 스레드에서 실행하고 끝날 때까지 기다린다 (제한 시간을 넘기면 실패).</summary>
    public static void Run(Func<Task> scenario, int timeoutSeconds = 120)
    {
        var task = Dispatcher.InvokeAsync(scenario).Task.Unwrap();
        if (!task.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
            throw new XunitException($"시나리오가 {timeoutSeconds}초 안에 끝나지 않았습니다 (UI 스레드가 멈췄을 수 있음).");
        task.GetAwaiter().GetResult();
    }

    private static Dispatcher Start()
    {
        // 자동화 모드: 창을 화면 밖에 포커스 없이 띄우고 전역 단축키·파일 관리자 등록을 건드리지 않는다
        Environment.SetEnvironmentVariable(AppPaths.AutomationVariable, "1");

        Dispatcher? dispatcher = null;
        Exception? failure = null;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var app = new App.App();
                app.InitializeComponent();
                // App.xaml의 종료 방식을 덮어쓴다: 시나리오마다 메인 창을 닫아도 앱(리소스)은 남아 있어야 한다
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                ready.Set();
            }

            if (failure is null) Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Scenario UI",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (failure is not null) throw new XunitException("앱 리소스를 불러오지 못했습니다: " + failure);
        return dispatcher!;
    }
}

/// <summary>모달 대화상자 대신 정해 둔 답을 하고, 무엇을 물었는지 기록한다 (테스트가 멈추지 않게).</summary>
internal sealed class FakeDialogs : IDialogService
{
    public List<string> Errors { get; } = [];

    public List<string> Asked { get; } = [];

    public bool ConfirmAnswer { get; set; } = true;

    public ConflictChoice ConflictAnswer { get; set; } = ConflictChoice.Overwrite;

    public void ShowError(string message) => Errors.Add(message);

    public bool Confirm(string message, string title)
    {
        Asked.Add(title + ": " + message);
        return ConfirmAnswer;
    }

    public bool? AskYesNoCancel(string message, string title)
    {
        Asked.Add(title + ": " + message);
        return ConfirmAnswer;
    }

    public string? Prompt(string title, string label, string initialValue) => null;

    public (ConflictChoice Choice, bool ApplyToAll) ResolveConflict(TransferConflict conflict) => (ConflictAnswer, true);

    public FilterSettings? EditFilter(FilterSettings current, IList<FilterSettings> presets) => null;

    public IReadOnlyList<RenamePreview>? BatchRename(IReadOnlyList<string> paths) => null;

    public CompareSession? PickSession(IList<CompareSession> sessions) => null;

    public AppPreferences? EditPreferences(AppPreferences current) => null;
}

/// <summary>
/// 앱 한 벌: 빈 임시 데이터 폴더(사용자 설정과 분리)에 설정을 써 두고 서비스·메인 창을 만들어 화면 밖에 띄운다.
/// 반드시 <see cref="UiThread.Run"/> 안에서 만들고 쓴다.
/// </summary>
internal sealed class AppHarness : IDisposable
{
    private AppHarness(string dataDir, FakeDialogs dialogs, ServiceProvider services, MainWindow window)
    {
        DataDir = dataDir;
        Dialogs = dialogs;
        Services = services;
        Window = window;
    }

    public string DataDir { get; }

    public FakeDialogs Dialogs { get; }

    public ServiceProvider Services { get; }

    public MainWindow Window { get; }

    public MainViewModel Vm => Window.ViewModel;

    /// <param name="configure">시작 설정 (창 구성·탭 경로·비교 옵션·모듈 켜기 등).</param>
    public static async Task<AppHarness> StartAsync(Action<AppSettings>? configure = null, IReadOnlyList<string>? args = null)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "sidebyside-scenario-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        Environment.SetEnvironmentVariable(AppPaths.DataDirVariable, dataDir);

        var initial = new AppSettings { Theme = ThemeMode.Light };
        configure?.Invoke(initial);
        new JsonSettingsStore(AppPaths.SettingsFile).Save(initial);

        var dialogs = new FakeDialogs();
        var services = App.App.ConfigureServices(s => s.AddSingleton<IDialogService>(dialogs));
        var settings = services.GetRequiredService<AppSettings>();
        L.Use(settings.Language);
        CommandRegistry.ApplyOverrides(settings.KeyBindings);
        var theme = services.GetRequiredService<ThemeManager>();
        theme.Accent = settings.AccentColor;
        theme.Apply(settings.Theme);

        var window = services.GetRequiredService<MainWindow>();
        Application.Current.MainWindow = window;
        window.Show();
        await window.ViewModel.InitializeAsync(args ?? []);
        return new AppHarness(dataDir, dialogs, services, window);
    }

    public void Execute(string commandId) => Vm.Commands.Execute(commandId);

    public bool CanExecute(string commandId) => Vm.Commands[commandId].CanExecute(null);

    /// <summary>창 하나의 선택을 화면을 통해 바꾸고(사용자가 고른 것과 같은 경로) 반영될 때까지 기다린다.</summary>
    public async Task SelectAsync(PaneViewModel pane, params string[] names)
    {
        Vm.Activate(pane);
        pane.ActiveTab.RequestSelect(names);
        await WaitUntilAsync(() => pane.ActiveTab.SelectedItems.Select(i => i.Name).OrderBy(n => n).SequenceEqual(names.OrderBy(n => n)),
            "선택 반영: " + string.Join(", ", names));
    }

    /// <summary>창 하나에서 전체 선택 (사용자의 Ctrl+A와 같이 목록 컨트롤이 직접 선택 — 자리 표시 행 포함).</summary>
    public async Task SelectAllAsync(PaneViewModel pane)
    {
        Vm.Activate(pane);
        var view = ReferenceEquals(pane, Vm.Left) ? Window.LeftPane : Window.RightPane;
        view.List.SelectAll();
        await WaitUntilAsync(() => pane.ActiveTab.SelectedItems.Count == pane.ActiveTab.Items.Count, "전체 선택 반영");
    }

    /// <summary>조건이 참이 될 때까지 기다린다 (UI 스레드의 다른 작업은 그 사이 계속 처리된다).</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 15000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new XunitException($"{what}: {timeoutMs:N0}ms 안에 이루어지지 않았습니다.");
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// UI가 입력을 처리할 수 있는지: 입력과 같은 우선순위의 빈 작업이 제한 시간 안에 실행되는지 본다.
    /// 화면은 그려지지만 클릭·키가 먹히지 않는 상태(높은 우선순위 작업이 끝없이 쌓임)를 잡는다.
    /// </summary>
    public static async Task AssertResponsiveAsync(string when, int limitMs = 500)
    {
        var input = Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Input).Task;
        if (await Task.WhenAny(input, Task.Delay(limitMs)) != input)
            throw new XunitException($"{when}: 입력 처리가 {limitMs}ms 넘게 밀렸습니다 (화면이 입력을 받지 못하는 상태).");
    }

    /// <summary>일정 시간 동안 계속 응답하는지 (짧은 간격으로 여러 번 확인).</summary>
    public static async Task StayResponsiveAsync(string when, int durationMs = 2000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < durationMs)
        {
            await AssertResponsiveAsync(when);
            await Task.Delay(100);
        }
    }

    public void Dispose()
    {
        try
        {
            Window.Close();
        }
        finally
        {
            Services.Dispose();
            Environment.SetEnvironmentVariable(AppPaths.DataDirVariable, null);
            TempFolder.TryDelete(DataDir);
        }
    }
}

/// <summary>시나리오용 임시 폴더 (끝나면 지운다).</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sidebyside-files-" + Guid.NewGuid().ToString("N"));

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Dir(params string[] parts)
    {
        var path = Combine(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string relative, string content = "x")
    {
        var path = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => TryDelete(Path);

    public static void TryDelete(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            // svn·git이 만든 읽기 전용 파일도 지울 수 있게
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) System.IO.File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 임시 폴더 정리 실패는 시나리오 결과와 무관
        }
    }
}

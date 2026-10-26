using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SideBySide.App.Commands;
using SideBySide.App.Infrastructure;
using SideBySide.App.Modules;
using SideBySide.App.Services;
using SideBySide.App.ViewModels;
using SideBySide.Core.Settings;
using SideBySide.Shell.Abstractions;
using SideBySide.Shell.Services;
using SideBySide.Core.Localization;

namespace SideBySide.App;

/// <summary>
/// 진입점. 의존성 구성(<see cref="ConfigureServices"/>) → 설정 로드 → 테마 → 메인 창.
/// 사용법: <c>SideBySide.exe [경로]</c> 또는 <c>SideBySide.exe &lt;왼쪽&gt; &lt;오른쪽&gt;</c> (폴더 비교 / 파일 비교).
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;

    public static IServiceProvider Services => ((App)Current)._services!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 모듈의 관리자 권한 작업 1회 실행 (UAC로 시작된 프로세스): 창 없이 처리하고 종료.
        // 오류 로그 등록보다 먼저 — 승격 프로세스는 사용자 쓰기 가능 경로에 파일을 쓰지 않는다 (링크 바꿔치기 차단)
        if (Elevation.TryRunElevated(e.Args) is { } elevatedExit)
        {
            Shutdown(elevatedExit);
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) ErrorLog.Write(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write(args.Exception);
            args.SetObserved();
        };

        if (HotkeyAgent.TryParse(e.Args, out var shortcut))
        {
            HotkeyAgent.Run(this, shortcut);
            return;
        }

        // 탐색기 "새 창에서 열기"로 휴지통·제어판 같은 가상 폴더가 오면 탐색기에 돌려준다 ("내 PC"는 직접 연다)
        if (e.Args.Length == 1 && TabViewModel.IsVirtualFolder(e.Args[0]))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", e.Args[0]) { UseShellExecute = false })?.Dispose();
            Shutdown();
            return;
        }

        _services = ConfigureServices();

        var settings = _services.GetRequiredService<AppSettings>();
        L.Use(settings.Language); // 다른 화면 문자열보다 먼저 (명령 표·XAML이 이 값을 읽는다)
        CommandRegistry.ApplyOverrides(settings.KeyBindings);
        var theme = _services.GetRequiredService<ThemeManager>();
        theme.Accent = settings.AccentColor;
        theme.Apply(settings.Theme);

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        _ = window.ViewModel.InitializeAsync(e.Args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services is not null)
        {
            try
            {
                _services.GetRequiredService<ISettingsStore>().Save(_services.GetRequiredService<AppSettings>());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 설정 저장 실패는 종료를 막지 않는다
            }

            // 모듈은 메인 창을 닫을 때 먼저 정리된다 (설정 저장 전). 여기서는 남은 것만 해제
            _services.Dispose();
        }

        base.OnExit(e);
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Core
        services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(JsonSettingsStore.DefaultPath));
        services.AddSingleton(sp => sp.GetRequiredService<ISettingsStore>().Load());

        // Shell (Windows 연동)
        services.AddSingleton<IOwnerWindow, OwnerWindow>();
        services.AddSingleton<IIconProvider, ShellIconProvider>();
        services.AddSingleton<IFileOperations, ShellFileOperations>();
        services.AddSingleton<IFileClipboard, ShellClipboard>();
        services.AddSingleton<IContextMenuService, ShellContextMenu>();
        services.AddSingleton<ILauncher, ShellLauncher>();
        services.AddSingleton<IDriveService, DriveService>();
        services.AddSingleton<IFileManagerRegistration, FileManagerRegistration>();

        // App
        services.AddSingleton<ThemeManager>();
        services.AddSingleton<ModuleManager>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<UndoService>();
        services.AddSingleton<FileTransferService>();
        services.AddSingleton<CommandHost>();
        services.AddSingleton<ExplorerContext>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 예기치 않은 오류로 창이 닫혀 작업을 잃지 않도록 기록·알리고 계속 실행
        ErrorLog.Write(e.Exception);
        MessageBox.Show(e.Exception.Message, L.T("SideBySide 오류"), MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

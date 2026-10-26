using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SideBySide.App;
using SideBySide.App.Dialogs;
using SideBySide.App.Modules;
using SideBySide.App.Services;
using SideBySide.App.Views;
using SideBySide.Core.Settings;

namespace SideBySide.Modules.Tests;

/// <summary>
/// 화면을 띄우지 않고 앱 리소스(테마·아이콘·레이아웃·도구 패널 스타일)를 실제로 불러 메인 창·설정 창·도구 패널·6개 모듈 화면을
/// 만들고 배치까지 해 본다. XAML 리소스 형식 오류처럼 빌드로는 잡히지 않고 실행해야 드러나는 문제를 잡는다.
/// 창을 표시하거나 네트워크 서비스를 시작하지 않는다.
/// </summary>
public class UiSmokeTests
{
    private sealed class FakeHost(string id) : IModuleHost
    {
        public string ModuleId => id;

        public Window? Owner => null;

        public string? ActiveFolder => null;

        public IReadOnlyList<string> SelectedPaths => [];

        public void NavigateTo(string path) { }

        public void RefreshFolder(string folder) { }

        public void ShowNotice(string text) { }

        public void ShowError(string message) => throw new InvalidOperationException(message);

        public bool Confirm(string message, string title) => false;

        public string? PickFolder(string title, string? initialFolder) => null;

        public IReadOnlyList<string> PickFiles(string title) => [];

        public T LoadData<T>() where T : class, new() => new();

        public void SaveData<T>(T data) where T : class { }

        public Task<ElevatedResult> RunElevatedAsync(string operation, string payloadJson, CancellationToken cancellationToken = default)
            => Task.FromResult(new ElevatedResult(true, null, null));

        public void BringToFront() { }
    }

    private static void Layout(FrameworkElement element, double width = 900, double height = 700)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    [Fact]
    public void Every_screen_loads_and_lays_out_in_both_themes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Run();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void Run()
    {
        var app = new SideBySide.App.App();
        app.InitializeComponent();

        var services = (ServiceProvider)typeof(SideBySide.App.App)
            .GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;
        try
        {
            var theme = services.GetRequiredService<ThemeManager>();
            var settings = services.GetRequiredService<AppSettings>();
            foreach (var (mode, accent) in new[] { (ThemeMode.Dark, "#E3762B"), (ThemeMode.Light, "Default"), (ThemeMode.Light, "System") })
            {
                theme.Accent = accent;
                theme.Apply(mode);

                // 메인 창 (도구 모음·도구 패널 포함)
                var main = services.GetRequiredService<MainWindow>();
                Layout(main, 1280, 800);

                // 모듈 화면: 앱 리소스로 템플릿을 적용하고 배치 (네트워크 서비스는 Open 전이라 시작되지 않음)
                foreach (var descriptor in ModuleCatalog.All)
                {
                    using var module = descriptor.Create(new FakeHost(descriptor.Id));
                    var host = new Border { Child = module.View };
                    Layout(host);
                    Layout(host, 360, 600); // 좁은 오른쪽 패널
                    host.Child = null;

                    var window = new ModuleWindow(descriptor.Title, module.View, new ModuleSettings(), theme);
                    Layout(window);
                    window.Content = null;
                    module.OnClosed();
                }

                // 도구 패널 탭 표시
                var manager = services.GetRequiredService<ModuleManager>();
                var panel = new ToolPanelHost { DataContext = manager };
                Layout(panel, 380, 700);

                // 설정 창 (모듈 탭·포인트 색 견본)
                var prefs = new AppPreferences(mode, AppLanguage.Korean, false, OpenShortcut.WinAltE, false, [], new Dictionary<string, string>(), accent,
                    ModuleCatalog.All.ToDictionary(m => m.Id, m => new ModulePreference(true, ModuleOpenMode.Default)));
                var dialog = new SettingsDialog(prefs);
                Layout(dialog, 800, 640);
                dialog.TabModules.IsChecked = true;
                Layout(dialog, 800, 640);
            }
        }
        finally
        {
            services.GetRequiredService<ModuleManager>().Dispose();
        }
    }
}

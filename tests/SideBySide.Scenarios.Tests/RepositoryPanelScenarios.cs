using System.Diagnostics;
using SideBySide.App.Commands;
using SideBySide.App.Modules;
using SideBySide.Core.Settings;
using Xunit.Abstractions;

namespace SideBySide.Scenarios.Tests;

/// <summary>
/// 한 창 모드에서 저장소 폴더에 들어가면 Git·SVN 패널이 자동으로 열리고, 벗어나거나 두 창으로 바꾸면 닫힌다.
/// 실제 git·svn으로 임시 저장소를 만든다 (설치돼 있지 않으면 건너뛴다).
/// </summary>
public class RepositoryPanelScenarios(ITestOutputHelper output)
{
    [Fact]
    public void Git_panel_follows_repository_folders() => RunAutoOpen(ModuleCatalog.GitId, folder =>
    {
        var git = FindTool("git.exe");
        if (git is null) return null;
        RunTool(git, folder, "init", "-q");
        return folder;
    });

    [Fact]
    public void Svn_panel_follows_working_copy_folders() => RunAutoOpen(ModuleCatalog.SvnId, folder =>
    {
        var svnadmin = FindTool("svnadmin.exe");
        var svn = FindTool("svn.exe");
        if (svnadmin is null || svn is null) return null;
        var repo = Path.Combine(Path.GetDirectoryName(folder)!, "svn-repo");
        RunTool(svnadmin, Path.GetDirectoryName(folder)!, "create", repo);
        RunTool(svn, Path.GetDirectoryName(folder)!, "checkout", "-q", new Uri(repo).AbsoluteUri, folder);
        return folder;
    });

    /// <param name="createRepository">저장소를 만들고 그 최상위를 돌려준다. 도구가 없으면 null.</param>
    private void RunAutoOpen(string moduleId, Func<string, string?> createRepository) => UiThread.Run(async () =>
    {
        using var files = new TempFolder();
        var plain = files.Dir("plain");
        var root = createRepository(files.Combine("repo"));
        if (root is null)
        {
            output.WriteLine($"{moduleId}: 명령줄 도구가 설치돼 있지 않아 건너뜁니다.");
            return;
        }

        var nested = Directory.CreateDirectory(Path.Combine(root, "src", "deep")).FullName;

        using var app = await AppHarness.StartAsync(s =>
        {
            s.DualPane = false;
            s.Left.Tabs = [plain];
            s.Modules[moduleId] = new ModuleSettings { Enabled = true, OpenMode = ModuleOpenMode.Panel };
        });
        var vm = app.Vm;
        bool Shown() => vm.Tools.PanelTools.Any(t => t.Id == moduleId);

        await AppHarness.WaitUntilAsync(() => vm.Left.ActiveTab.Path == plain, "시작 폴더");
        Assert.False(Shown());

        await vm.Left.ActiveTab.NavigateAsync(nested);
        await AppHarness.WaitUntilAsync(Shown, "저장소 안으로 들어가면 패널 열림");

        await vm.Left.ActiveTab.NavigateAsync(plain);
        await AppHarness.WaitUntilAsync(() => !Shown(), "저장소 밖으로 나가면 패널 닫힘");

        await vm.Left.ActiveTab.NavigateAsync(root);
        await AppHarness.WaitUntilAsync(Shown, "다시 들어가면 패널 열림");

        app.Execute(CommandIds.ToggleDualPane);
        await AppHarness.WaitUntilAsync(() => !Shown(), "두 창으로 바꾸면 패널 닫힘");
        await AppHarness.AssertResponsiveAsync("패널 열고 닫은 뒤");
        Assert.Empty(app.Dialogs.Errors);
    });

    /// <summary>PATH에서 찾고, 없으면 흔한 설치 위치(Git for Windows, TortoiseSVN, SlikSVN)를 본다.</summary>
    private static string? FindTool(string exe)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var programFiles in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(programFiles);
            if (root.Length == 0) continue;
            dirs.Add(Path.Combine(root, "Git", "cmd"));
            dirs.Add(Path.Combine(root, "TortoiseSVN", "bin"));
            dirs.Add(Path.Combine(root, "SlikSvn", "bin"));
        }

        foreach (var dir in dirs)
        {
            try
            {
                var path = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(path)) return path;
            }
            catch (ArgumentException)
            {
                // 잘못된 PATH 항목
            }
        }

        return null;
    }

    private static void RunTool(string exe, string workingDirectory, params string[] args)
    {
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        if (exe.EndsWith("svn.exe", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add("--non-interactive");
        Directory.CreateDirectory(workingDirectory);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} {string.Join(' ', args)} 실패: {error}");
    }
}

using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using SideBySide.App.Commands;
using SideBySide.App.Views;

namespace SideBySide.Scenarios.Tests;

/// <summary>두 창·비교 모드 시나리오 (이전에 실제로 생긴 문제의 회귀 방지).</summary>
public class PaneScenarios
{
    /// <summary>
    /// 비교 모드('차이만' + 하위 폴더 비교)에서 오른쪽 폴더를 왼쪽으로 복사한 직후, 두 창이 선택을 끝없이 주고받아
    /// 화면이 입력을 받지 못하던 문제 (v1.7.1에서 수정).
    /// </summary>
    [Fact]
    public void Compare_mode_copy_keeps_ui_responsive_and_settles() => UiThread.Run(async () =>
    {
        using var files = new TempFolder();
        var left = files.Dir("left");
        files.File(@"left\readme.txt", "left only");
        var right = files.Dir("right");
        for (int d = 0; d < 6; d++)
        {
            for (int f = 0; f < 15; f++) files.File($@"right\ControlRoom\sub{d}\deep\f{f}.cs", "// code\n");
        }

        files.File(@"right\ControlRoom.meta", "meta");

        using var app = await AppHarness.StartAsync(s =>
        {
            s.DualPane = true;
            s.CompareMode = true;
            s.ShowDifferencesOnly = true;
            s.Left.Tabs = [left];
            s.Right.Tabs = [right];
        });
        var vm = app.Vm;
        await AppHarness.WaitUntilAsync(() => vm.Compare.IsActive && !vm.Compare.IsRunning
                                               && vm.Right.ActiveTab.Items.Any(i => i.Name == "ControlRoom"), "첫 비교");

        // 사용자가 오른쪽에서 Ctrl+A(왼쪽에만 있는 readme 줄의 자리 표시까지 선택) 후 Ctrl+← (= 왼쪽으로 복사).
        // 복사 뒤 다시 비교할 때 이 선택을 두 창에 이름으로 다시 적용하는데, readme는 왼쪽에선 실제 파일·오른쪽에선 자리 표시라
        // 두 창 선택이 달라지고, 서로 맞추려는 요청이 엇갈려 끝없이 반복됐다.
        await app.SelectAllAsync(vm.Right);
        Assert.True(app.CanExecute(CommandIds.CopyToLeft));
        app.Execute(CommandIds.CopyToLeft);

        await AppHarness.WaitUntilAsync(() => Directory.Exists(Path.Combine(left, "ControlRoom")) && File.Exists(Path.Combine(left, "ControlRoom.meta")),
            "왼쪽으로 복사");
        await AppHarness.StayResponsiveAsync("복사 직후 다시 비교하는 동안", 3000);

        // 같아진 항목은 '차이만'에서 빠지고, 두 창 줄 수가 맞아야 한다
        await AppHarness.WaitUntilAsync(() => !vm.Compare.IsRunning
                                               && vm.Left.ActiveTab.Items.All(i => i.Name != "ControlRoom")
                                               && vm.Left.ActiveTab.Items.Count == vm.Right.ActiveTab.Items.Count,
            "복사 뒤 비교 결과 정리");
        await AppHarness.AssertResponsiveAsync("비교 정리 후");
        Assert.Empty(app.Dialogs.Errors);
    });

    [Fact]
    public void Swap_panes_exchanges_tabs_and_recompares() => UiThread.Run(async () =>
    {
        using var files = new TempFolder();
        var a = files.Dir("a");
        var b = files.Dir("b");
        var c = files.Dir("c");
        files.File(@"a\only-a.txt");
        files.File(@"c\only-c.txt");

        using var app = await AppHarness.StartAsync(s =>
        {
            s.DualPane = true;
            s.Left.Tabs = [a];
            s.Right.Tabs = [b, c];
            s.Right.ActiveTab = 1;
        });
        var vm = app.Vm;
        await AppHarness.WaitUntilAsync(() => vm.Right.ActiveTab.Path == c, "시작 탭");

        app.Execute(CommandIds.SwapPanes);
        Assert.Equal([b, c], vm.Left.Tabs.Select(t => t.Path));
        Assert.Equal(c, vm.Left.ActiveTab.Path);
        Assert.Equal([a], vm.Right.Tabs.Select(t => t.Path));

        // 비교 중에 바꾸면 바꾼 뒤 다시 비교한다
        app.Execute(CommandIds.ToggleCompare);
        await AppHarness.WaitUntilAsync(() => vm.Compare.IsActive && !vm.Compare.IsRunning, "비교 시작");
        app.Execute(CommandIds.SwapPanes);
        await AppHarness.WaitUntilAsync(() => vm.Compare.IsActive && !vm.Compare.IsRunning && vm.Left.ActiveTab.Path == a, "바꾼 뒤 다시 비교");
        await AppHarness.WaitUntilAsync(() => vm.Left.ActiveTab.Items.Any(i => i.Name == "only-a.txt") && vm.Right.ActiveTab.Items.Any(i => i.Name == "only-c.txt"),
            "바뀐 쪽 기준 비교 결과");
        await AppHarness.AssertResponsiveAsync("창 바꾸기 후");
    });

    /// <summary>파일 목록이 Ctrl+←/→를 먼저 가로채 반대쪽 창 복사 단축키가 실행되지 않던 문제 (v1.6.1에서 수정).</summary>
    [Fact]
    public void File_list_hands_bound_ctrl_arrows_to_the_window() => UiThread.Run(() =>
    {
        var enabled = true;
        var bindings = new InputBindingCollection
        {
            new KeyBinding(new RelayCommand(() => { }, () => enabled), Key.Right, ModifierKeys.Control),
        };

        Assert.True(FileListView.IsWindowShortcut(Key.Right, ModifierKeys.Control, bindings));
        Assert.False(FileListView.IsWindowShortcut(Key.Right, ModifierKeys.None, bindings));      // 수식 키 없는 이동은 목록 몫
        Assert.False(FileListView.IsWindowShortcut(Key.Left, ModifierKeys.Control, bindings));    // 지정되지 않은 조합
        Assert.False(FileListView.IsWindowShortcut(Key.A, ModifierKeys.Control, bindings));       // 이동 키가 아님
        enabled = false;
        Assert.False(FileListView.IsWindowShortcut(Key.Right, ModifierKeys.Control, bindings));   // 지금 실행할 수 없으면 목록 몫
        return Task.CompletedTask;
    });

    /// <summary>실제 메인 창 단축키 표에서: 두 창일 때만 Ctrl+→가 창 단축키(오른쪽으로 복사)로 넘어간다.</summary>
    [Fact]
    public void Ctrl_arrow_goes_to_the_window_only_in_dual_pane() => UiThread.Run(async () =>
    {
        using var files = new TempFolder();
        using var app = await AppHarness.StartAsync(s =>
        {
            s.DualPane = true;
            s.Left.Tabs = [files.Dir("l")];
            s.Right.Tabs = [files.Dir("r")];
        });

        Assert.True(FileListView.IsWindowShortcut(Key.Right, ModifierKeys.Control, app.Window.InputBindings));
        Assert.True(FileListView.IsWindowShortcut(Key.Left, ModifierKeys.Control | ModifierKeys.Shift, app.Window.InputBindings));

        app.Execute(CommandIds.ToggleDualPane);
        await AppHarness.WaitUntilAsync(() => !app.Vm.IsDualPane, "한 창으로");
        Assert.False(FileListView.IsWindowShortcut(Key.Right, ModifierKeys.Control, app.Window.InputBindings));
    });
}

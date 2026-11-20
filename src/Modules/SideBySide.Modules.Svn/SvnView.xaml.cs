using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SideBySide.Modules.Svn;

public partial class SvnView : UserControl
{
    private Window? _window;

    public SvnView() => InitializeComponent();

    private SvnViewModel? ViewModel => DataContext as SvnViewModel;

    // 화면은 오른쪽 패널과 별도 창 사이를 옮겨 다닐 수 있다: 들어 있는 창이 활성화될 때마다 상태를 다시 읽는다
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Detach();
        _window = Window.GetWindow(this);
        if (_window is not null) _window.Activated += OnWindowActivated;
        ViewModel?.ScheduleRefresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void Detach()
    {
        if (_window is not null) _window.Activated -= OnWindowActivated;
        _window = null;
    }

    private void OnWindowActivated(object? sender, EventArgs e) => ViewModel?.ScheduleRefresh();

    private void OnNewBranchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is not { } vm) return;
        e.Handled = true;
        if (vm.CreateBranchCommand.CanExecute(null)) vm.CreateBranchCommand.Execute(null);
    }

    /// <summary>요약 칸에서 Ctrl+Enter로 커밋.</summary>
    private void OnSummaryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control || ViewModel is not { } vm) return;
        e.Handled = true;
        if (vm.CanCommit && vm.CommitCommand.CanExecute(null)) vm.CommitCommand.Execute(null);
    }
}

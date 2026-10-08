using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SideBySide.App.Commands;
using SideBySide.App.ViewModels;

namespace SideBySide.App.Views;

/// <summary>파일 비교/병합 화면. DataContext = <see cref="FileDiffViewModel"/> (닫혀 있으면 null).</summary>
public partial class FileDiffView : UserControl
{
    private FileDiffViewModel? _vm;

    public FileDiffView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.ScrollRequested -= OnScrollRequested;
            _vm = DataContext as FileDiffViewModel;
            if (_vm is not null) _vm.ScrollRequested += OnScrollRequested;
        };
    }

    /// <summary>파일 비교 범위 단축키 연결 (MainWindow가 설정).</summary>
    public CommandHost? Commands
    {
        set => value?.BindKeys(this, CommandScope.Diff);
    }

    public void FocusContent()
    {
        if (_vm?.IsEditing == true) LeftEditor.Focus();
        else if (Lines.SelectedItem is { } item && Lines.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container) container.Focus();
        else Lines.Focus();
    }

    private void OnScrollRequested(object? sender, int row)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (row < 0 || row >= Lines.Items.Count) return;
            // 차이 위쪽에 몇 줄 여유를 두고 보여준다
            Lines.ScrollIntoView(Lines.Items[Math.Max(0, row - 3)]);
            Lines.ScrollIntoView(Lines.Items[Math.Min(Lines.Items.Count - 1, row + 8)]);
            Lines.ScrollIntoView(Lines.Items[row]);
            if (IsKeyboardFocusWithin && Lines.ItemContainerGenerator.ContainerFromIndex(row) is ListBoxItem container) container.Focus();
        }, DispatcherPriority.Loaded);
    }
}

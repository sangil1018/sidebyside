using System.Windows;
using SideBySide.App.Dialogs;
using SideBySide.Core.Filtering;
using SideBySide.Core.Sessions;

namespace SideBySide.App.Services;

public sealed class DialogService(ThemeManager theme) : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow;

    public void ShowError(string message)
        => MessageBox.Show(Owner!, message, "SideBySide", MessageBoxButton.OK, MessageBoxImage.Warning);

    public bool Confirm(string message, string title)
        => MessageBox.Show(Owner!, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public bool? AskYesNoCancel(string message, string title)
        => MessageBox.Show(Owner!, message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

    public string? Prompt(string title, string label, string initialValue)
    {
        var dialog = Prepare(new InputDialog(title, label, initialValue));
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    public FilterSettings? EditFilter(FilterSettings current, IList<FilterSettings> presets)
    {
        var dialog = Prepare(new FilterDialog(current, presets));
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public bool BatchRename(IReadOnlyList<string> paths)
        => Prepare(new BatchRenameDialog(paths)).ShowDialog() == true;

    public CompareSession? PickSession(IList<CompareSession> sessions)
    {
        var dialog = Prepare(new SessionDialog(sessions));
        return dialog.ShowDialog() == true ? dialog.Selected : null;
    }

    private T Prepare<T>(T window)
        where T : Window
    {
        window.Owner = Owner;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        theme.ApplyToWindow(window);
        return window;
    }
}

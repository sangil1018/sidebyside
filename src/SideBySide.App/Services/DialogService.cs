using System.Windows;
using SideBySide.App.Dialogs;
using SideBySide.Core.FileSystem;
using SideBySide.Core.Filtering;
using SideBySide.Core.Rename;
using SideBySide.Core.Sessions;

namespace SideBySide.App.Services;

public sealed class DialogService(ThemeManager theme) : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow;

    public void ShowError(string message)
        => Show(message, "SideBySide", MessageBoxButton.OK, MessageBoxImage.Warning);

    public bool Confirm(string message, string title)
        => Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public bool? AskYesNoCancel(string message, string title)
        => Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
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

    public (ConflictChoice Choice, bool ApplyToAll) ResolveConflict(TransferConflict conflict)
    {
        var dialog = Prepare(new ConflictDialog(conflict));
        return dialog.ShowDialog() == true ? (dialog.Choice, dialog.ApplyToAll) : (ConflictChoice.Cancel, false);
    }

    public FilterSettings? EditFilter(FilterSettings current, IList<FilterSettings> presets)
    {
        var dialog = Prepare(new FilterDialog(current, presets));
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public IReadOnlyList<RenamePreview>? BatchRename(IReadOnlyList<string> paths)
    {
        var dialog = Prepare(new BatchRenameDialog(paths));
        return dialog.ShowDialog() == true ? dialog.Applied : null;
    }

    public CompareSession? PickSession(IList<CompareSession> sessions)
    {
        var dialog = Prepare(new SessionDialog(sessions));
        return dialog.ShowDialog() == true ? dialog.Selected : null;
    }

    public AppPreferences? EditPreferences(AppPreferences current)
    {
        // 포인트 색은 고르는 즉시 미리 보여 준다 (취소하면 호출자가 되돌린다)
        var dialog = Prepare(new SettingsDialog(current, accent =>
        {
            theme.Accent = accent;
            theme.Apply(theme.Mode);
        }));
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
        => Owner is { IsLoaded: true } owner
            ? MessageBox.Show(owner, message, title, buttons, image)
            : MessageBox.Show(message, title, buttons, image);

    private T Prepare<T>(T window)
        where T : Window
    {
        window.Owner = Owner;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        theme.ApplyToWindow(window);
        return window;
    }
}

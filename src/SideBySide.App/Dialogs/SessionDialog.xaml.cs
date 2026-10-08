using System.Windows;
using SideBySide.Core.Sessions;

namespace SideBySide.App.Dialogs;

public partial class SessionDialog : Window
{
    private readonly IList<CompareSession> _sessions;

    public SessionDialog(IList<CompareSession> sessions)
    {
        _sessions = sessions;
        InitializeComponent();
        Refresh();
        Loaded += (_, _) => Sessions.Focus();
    }

    public CompareSession? Selected { get; private set; }

    private void Refresh()
    {
        Sessions.ItemsSource = _sessions.ToList();
        if (_sessions.Count > 0) Sessions.SelectedIndex = 0;
        EmptyText.Visibility = _sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (Sessions.SelectedItem is not CompareSession session) return;
        Selected = session;
        DialogResult = true;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Sessions.SelectedItem is not CompareSession session) return;
        _sessions.Remove(session);
        Refresh();
    }
}

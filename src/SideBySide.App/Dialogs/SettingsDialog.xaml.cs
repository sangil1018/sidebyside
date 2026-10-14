using System.Windows;
using SideBySide.App.Services;
using SideBySide.Core.Settings;

namespace SideBySide.App.Dialogs;

public partial class SettingsDialog : Window
{
    public SettingsDialog(AppPreferences current)
    {
        InitializeComponent();
        (current.Theme switch
        {
            ThemeMode.Light => ThemeLight,
            ThemeMode.Dark => ThemeDark,
            _ => ThemeSystem,
        }).IsChecked = true;
        DefaultManager.IsChecked = current.DefaultFileManager;
        (current.OpenShortcut switch
        {
            OpenShortcut.WinCtrlE => ShortcutWinCtrlE,
            OpenShortcut.WinShiftE => ShortcutWinShiftE,
            _ => ShortcutWinAltE,
        }).IsChecked = true;
    }

    public AppPreferences? Result { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var theme = ThemeDark.IsChecked == true ? ThemeMode.Dark
            : ThemeLight.IsChecked == true ? ThemeMode.Light
            : ThemeMode.System;
        var shortcut = ShortcutWinCtrlE.IsChecked == true ? OpenShortcut.WinCtrlE
            : ShortcutWinShiftE.IsChecked == true ? OpenShortcut.WinShiftE
            : OpenShortcut.WinAltE;
        Result = new AppPreferences(theme, DefaultManager.IsChecked == true, shortcut);
        DialogResult = true;
    }
}

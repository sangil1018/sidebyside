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
    }

    public AppPreferences? Result { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var theme = ThemeDark.IsChecked == true ? ThemeMode.Dark
            : ThemeLight.IsChecked == true ? ThemeMode.Light
            : ThemeMode.System;
        Result = new AppPreferences(theme, DefaultManager.IsChecked == true);
        DialogResult = true;
    }
}

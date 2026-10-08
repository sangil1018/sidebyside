using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using SideBySide.Core.Settings;
using SideBySide.Shell.Services;

namespace SideBySide.App.Services;

/// <summary>
/// 라이트/다크 테마 전환. 색은 Themes/Tokens.*.xaml에만 정의되어 있고,
/// 컨트롤 스타일은 DynamicResource로 참조하므로 사전 하나만 바꾸면 전체가 바뀐다.
/// </summary>
public sealed class ThemeManager
{
    private const string TokensPrefix = "Themes/Tokens.";
    private ThemeMode _mode = ThemeMode.System;

    public ThemeManager() => SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

    public bool IsDark { get; private set; }

    public ThemeMode Mode => _mode;

    public event EventHandler? Changed;

    public void Apply(ThemeMode mode)
    {
        _mode = mode;
        var dark = mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemPrefersDark());
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains(TokensPrefix, StringComparison.Ordinal) == true);
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/SideBySide;component/{TokensPrefix}{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute),
        };

        if (existing is null) dictionaries.Insert(0, replacement);
        else dictionaries[dictionaries.IndexOf(existing)] = replacement;

        IsDark = dark;
        foreach (Window window in Application.Current.Windows) ApplyToWindow(window);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>다음 순서로 순환: 시스템 → 라이트 → 다크.</summary>
    public ThemeMode Cycle()
    {
        var next = _mode switch
        {
            ThemeMode.System => IsDark ? ThemeMode.Light : ThemeMode.Dark,
            ThemeMode.Light => ThemeMode.Dark,
            _ => ThemeMode.Light,
        };
        Apply(next);
        return next;
    }

    public void ApplyToWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            window.SourceInitialized += (_, _) => WindowTheming.SetDarkTitleBar(new WindowInteropHelper(window).Handle, IsDark);
            return;
        }

        WindowTheming.SetDarkTitleBar(hwnd, IsDark);
    }

    private static bool SystemPrefersDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_mode == ThemeMode.System && e.Category == UserPreferenceCategory.General)
            Application.Current?.Dispatcher.BeginInvoke(() => Apply(ThemeMode.System));
    }
}

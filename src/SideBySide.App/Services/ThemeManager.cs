using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using SideBySide.Core.Settings;
using SideBySide.Core.Theming;
using SideBySide.Shell.Services;

namespace SideBySide.App.Services;

/// <summary>
/// 라이트/다크 테마 전환. 색은 Themes/Tokens.*.xaml에만 정의되어 있고,
/// 컨트롤 스타일은 DynamicResource로 참조하므로 사전 하나만 바꾸면 전체가 바뀐다.
/// 포인트(강조) 색을 고르면 토큰 사전 뒤에 강조색 사전을 하나 더 넣어 강조색에서 파생되는 토큰만 덮어쓴다.
/// </summary>
public sealed class ThemeManager
{
    private const string TokensPrefix = "Themes/Tokens.";
    private ThemeMode _mode = ThemeMode.System;
    private ResourceDictionary? _accentOverlay;

    public ThemeManager() => SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

    public bool IsDark { get; private set; }

    public ThemeMode Mode => _mode;

    /// <summary>포인트 색 설정 값 (<see cref="AccentPalette.Default"/>, <see cref="AccentPalette.System"/>, "#RRGGBB"). 바꾼 뒤 <see cref="Apply"/>.</summary>
    public string Accent { get; set; } = AccentPalette.Default;

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
        ApplyAccent(dictionaries, replacement);
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

    /// <summary>현재 설정의 기준 색. 기본(토큰 그대로)이면 null.</summary>
    public static Rgb? ResolveAccent(string setting)
    {
        if (string.Equals(setting, AccentPalette.System, StringComparison.OrdinalIgnoreCase)) return SystemAccent();
        return Rgb.TryParse(setting, out var color) ? color : null;
    }

    /// <summary>강조색 사전: 토큰 사전 바로 뒤에 두어 같은 키를 덮어쓴다. 기본 강조색이면 없앤다.</summary>
    private void ApplyAccent(IList<ResourceDictionary> dictionaries, ResourceDictionary tokens)
    {
        if (_accentOverlay is not null) dictionaries.Remove(_accentOverlay);
        _accentOverlay = null;

        if (ResolveAccent(Accent) is not { } baseColor) return;
        var surface = tokens["Brush.SurfaceBg"] is SolidColorBrush s ? ToRgb(s.Color) : new Rgb(0xFF, 0xFF, 0xFF);
        var set = AccentPalette.Build(baseColor, IsDark, surface);

        _accentOverlay = new ResourceDictionary
        {
            ["Brush.Accent"] = Freeze(set.Accent),
            ["Brush.AccentText"] = Freeze(set.AccentText),
            ["Brush.AccentSubtle"] = Freeze(set.AccentSubtle),
            ["Brush.Selected"] = Freeze(set.Selected),
            ["Brush.Newer"] = Freeze(set.Accent),
            ["Brush.DiffLeftMark"] = Freeze(set.Accent),
        };
        dictionaries.Insert(dictionaries.IndexOf(tokens) + 1, _accentOverlay);
    }

    private static SolidColorBrush Freeze(Rgb c)
    {
        var brush = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    private static Rgb ToRgb(Color c) => new(c.R, c.G, c.B);

    /// <summary>Windows 강조색 (DWM 레지스트리, ABGR). 읽을 수 없으면 null.</summary>
    private static Rgb? SystemAccent()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
        if (key?.GetValue("AccentColor") is not int abgr) return null;
        return new Rgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
    }

    private static bool SystemPrefersDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // 시스템 테마나 Windows 강조색이 바뀌면 다시 적용 (강조색 변경은 General 범주로 온다)
        var followsSystem = _mode == ThemeMode.System || string.Equals(Accent, AccentPalette.System, StringComparison.OrdinalIgnoreCase);
        if (followsSystem && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            Application.Current?.Dispatcher.BeginInvoke(() => Apply(_mode));
    }
}

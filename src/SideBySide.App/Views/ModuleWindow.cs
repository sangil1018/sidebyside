using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Imaging;
using SideBySide.App.Services;
using SideBySide.Core.Settings;

namespace SideBySide.App.Views;

/// <summary>
/// 모듈을 별도 창으로 열 때의 창. 앱 테마·리소스를 그대로 쓰고, 위치·크기는 모듈 설정에 저장한다.
/// 화면(<c>view</c>)은 모듈 소유라 닫을 때 떼어 내고 창만 버린다.
/// </summary>
public sealed class ModuleWindow : Window
{
    private readonly ModuleSettings _settings;
    private bool _silent;

    public ModuleWindow(string title, FrameworkElement view, ModuleSettings settings, ThemeManager theme)
    {
        _settings = settings;
        Title = title;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/SideBySide;component/Assets/app.ico", UriKind.Absolute));
        SetResourceReference(BackgroundProperty, "Brush.SurfaceBg");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        FontFamily = (System.Windows.Media.FontFamily)FindResource("Font.UI");
        FontSize = (double)FindResource("FontSize.Body");
        UseLayoutRounding = true;
        System.Windows.Media.TextOptions.SetTextFormattingMode(this, System.Windows.Media.TextFormattingMode.Display);
        MinWidth = 360;
        MinHeight = 320;
        ShowInTaskbar = true;

        var p = settings.Window;
        Width = p is { Width: > 0 } ? p.Width : 960;
        Height = p is { Height: > 0 } ? p.Height : 680;
        if (p is not null && !double.IsNaN(p.Left) && !double.IsNaN(p.Top) && IsOnScreen(p))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = p.Left;
            Top = p.Top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        if (p?.Maximized == true) WindowState = WindowState.Maximized;
        Content = view;
        theme.ApplyToWindow(this);
        theme.Changed += OnThemeChanged;
        Closed += (_, _) => theme.Changed -= OnThemeChanged;

        void OnThemeChanged(object? sender, EventArgs e) => theme.ApplyToWindow(this);
    }

    /// <summary>사용자가 닫으려 한다 (Cancel로 막을 수 있다).</summary>
    public event EventHandler<CancelEventArgs>? CloseRequested;

    /// <summary>사용자가 닫았다 (<see cref="CloseWithoutNotify"/>로 닫은 경우는 제외).</summary>
    public event EventHandler? ClosedByUser;

    /// <summary>앱이 닫는다 (패널로 옮기기, 모듈 끄기): 묻지 않고 알림 없이.</summary>
    public void CloseWithoutNotify()
    {
        _silent = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_silent)
        {
            CloseRequested?.Invoke(this, e);
            if (e.Cancel) return;
        }

        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.Window = new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Content = null; // 모듈 화면을 다른 곳(패널)에 다시 붙일 수 있게
        base.OnClosed(e);
        if (!_silent) ClosedByUser?.Invoke(this, EventArgs.Empty);
        Owner?.Activate();
    }

    private static bool IsOnScreen(WindowPlacement p)
        => p.Left >= SystemParameters.VirtualScreenLeft - 50
           && p.Top >= SystemParameters.VirtualScreenTop - 50
           && p.Left + 100 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
           && p.Top + 50 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
}

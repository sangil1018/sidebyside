using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SideBySide.App.ViewModels;
using SideBySide.Shell.Preview;

namespace SideBySide.App.Views;

/// <summary>
/// 미리보기 창. 이미지/텍스트는 WPF로, 그 외(PDF, Office 등)는 Windows 미리보기 처리기를 호스팅한다.
/// 처리기는 별도 HWND라 WPF 위에 겹칠 수 없으므로(airspace) 필요할 때만 만든다.
/// </summary>
public partial class PreviewPane : UserControl
{
    private PreviewViewModel? _vm;
    private PreviewHandlerHost? _host;

    public PreviewPane()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnPropertyChanged;
            _vm = DataContext as PreviewViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnPropertyChanged;
            Update();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) CloseHost();
        };
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PreviewViewModel.Kind) or nameof(PreviewViewModel.ShellPath)) Update();
    }

    private void Update()
    {
        var kind = _vm?.Kind ?? PreviewKind.None;
        Placeholder.Text = kind switch
        {
            PreviewKind.None => _vm?.Title.Length > 0 ? string.Empty : "미리 볼 파일을 선택하세요.",
            PreviewKind.Unsupported => "이 파일은 미리 볼 수 없습니다.",
            _ => string.Empty,
        };

        if (kind == PreviewKind.Shell && _vm?.ShellPath is { } path)
        {
            _host ??= new PreviewHandlerHost();
            if (ShellHost.Child is null) ShellHost.Child = _host;
            ShellHost.Visibility = Visibility.Visible;

            // 호스트 창이 만들어진 뒤 로드
            Dispatcher.BeginInvoke(() =>
            {
                if (_vm?.ShellPath == path && !_host.Open(path)) _vm.ShellPreviewFailed();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            _host?.Unload();
            ShellHost.Visibility = Visibility.Collapsed;
        }
    }

    private void CloseHost()
    {
        if (_host is null) return;
        _host.Unload();
        ShellHost.Child = null;
        _host.Dispose();
        _host = null;
    }
}

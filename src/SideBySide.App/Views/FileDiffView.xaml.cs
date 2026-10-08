using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
            if (_vm is not null)
            {
                _vm.ScrollRequested -= OnScrollRequested;
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _vm = DataContext as FileDiffViewModel;
            if (_vm is not null)
            {
                _vm.ScrollRequested += OnScrollRequested;
                _vm.PropertyChanged += OnViewModelPropertyChanged;
            }

            UpdateHorizontalScroll();
        };
    }

    private const double LineNumberWidth = 52;
    private const double TextMargin = 10;
    private double _charWidth;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileDiffViewModel.MaxLineLength) or nameof(FileDiffViewModel.IsEditing) or nameof(FileDiffViewModel.IsText))
            UpdateHorizontalScroll();
    }

    private void OnLinesSizeChanged(object sender, SizeChangedEventArgs e) => UpdateHorizontalScroll();

    /// <summary>가장 긴 줄 너비와 한쪽 텍스트 칸 너비로 가로 스크롤 범위를 계산한다 (고정폭 글꼴 기준).</summary>
    private void UpdateHorizontalScroll()
    {
        if (_vm is null || !_vm.IsText || _vm.IsEditing)
        {
            HScroll.Visibility = Visibility.Collapsed;
            return;
        }

        if (_charWidth <= 0)
        {
            var text = new FormattedText("0", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(Lines.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                Lines.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            _charWidth = text.WidthIncludingTrailingWhitespace;
        }

        var columnWidth = Math.Max(0, ((Lines.ActualWidth - 12) / 2) - LineNumberWidth - TextMargin);
        var contentWidth = (_vm.MaxLineLength * _charWidth) + 16;
        var max = Math.Max(0, contentWidth - columnWidth);

        HScroll.Maximum = max;
        HScroll.ViewportSize = columnWidth;
        HScroll.LargeChange = Math.Max(_charWidth, columnWidth * 0.8);
        HScroll.SmallChange = _charWidth * 4;
        HScroll.Visibility = max > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_vm.HorizontalOffset > max) _vm.HorizontalOffset = max;
    }

    private void OnLinesMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift || HScroll.Visibility != Visibility.Visible || _vm is null) return;
        _vm.HorizontalOffset = Math.Clamp(_vm.HorizontalOffset - (e.Delta / 120.0 * HScroll.SmallChange * 3), 0, HScroll.Maximum);
        e.Handled = true;
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

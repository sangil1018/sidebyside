using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace SideBySide.Modules.TempCleaner;

public partial class CleanerView : UserControl
{
    /// <summary>바이트 → "1.2 MB".</summary>
    public static readonly IValueConverter SizeConverter = new BytesConverter();

    public CleanerView(CleanerViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // 카테고리를 묶음(임시 파일 / 시스템 캐시 / ...)별로 표시
        var view = CollectionViewSource.GetDefaultView(vm.Categories);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CategoryViewModel.Group)));
    }

    private void OnDeleteSelected(object sender, RoutedEventArgs e)
    {
        if (DataContext is CleanerViewModel vm) _ = vm.DeleteFilesAsync(FileList.SelectedItems.Cast<CleanFile>().ToList());
    }

    private sealed class BytesConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is long b ? Cleaner.FormatBytes(b) : string.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

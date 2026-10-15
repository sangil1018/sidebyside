using System.Windows;
using SideBySide.Core.Rename;
using SideBySide.Core.Localization;

namespace SideBySide.App.Dialogs;

/// <summary>다중 이름 바꾸기. 입력할 때마다 미리보기를 갱신하고, 문제가 없을 때만 적용할 수 있다.</summary>
public partial class BatchRenameDialog : Window
{
    private readonly IReadOnlyList<string> _paths;
    private IReadOnlyList<RenamePreview> _preview = [];

    public BatchRenameDialog(IReadOnlyList<string> paths)
    {
        _paths = paths;
        InitializeComponent();
        UpdatePreview();
        Loaded += (_, _) => FindBox.Focus();
    }

    /// <summary>실제로 바꾼 항목 (실행 취소용).</summary>
    public IReadOnlyList<RenamePreview> Applied { get; private set; } = [];

    private void OnChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (!IsInitialized || PreviewList is null) return;
        var options = new RenameOptions
        {
            Find = FindBox.Text,
            Replace = ReplaceBox.Text,
            UseRegex = RegexBox.IsChecked == true,
            MatchCase = CaseBox.IsChecked == true,
            Template = string.IsNullOrWhiteSpace(TemplateBox.Text) ? "{name}{ext}" : TemplateBox.Text,
            StartNumber = int.TryParse(StartBox.Text, out var n) ? n : 1,
        };

        _preview = BatchRenamer.Preview(_paths, options);
        PreviewList.ItemsSource = _preview;

        var errors = _preview.Count(p => p.Error is not null);
        var changed = _preview.Count(p => p.IsChanged);
        SummaryText.Text = errors > 0 ? L.F("문제 {0}개를 해결해야 합니다.", errors) : L.F("{0}개 중 {1}개 변경", _preview.Count, changed);
        ApplyButton.IsEnabled = errors == 0 && changed > 0;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        try
        {
            BatchRenamer.Apply(_preview);
            Applied = _preview.Where(p => p.IsChanged).ToList();
            DialogResult = true;
        }
        catch (BatchRenameException ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdatePreview();
        }
    }
}

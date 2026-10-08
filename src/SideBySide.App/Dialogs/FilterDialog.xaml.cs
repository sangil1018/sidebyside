using System.Windows;
using System.Windows.Controls;
using SideBySide.Core.Filtering;

namespace SideBySide.App.Dialogs;

/// <summary>필터 편집. 프리셋 목록(설정에 저장되는 리스트)을 직접 수정한다.</summary>
public partial class FilterDialog : Window
{
    private readonly IList<FilterSettings> _presets;

    public FilterDialog(FilterSettings current, IList<FilterSettings> presets)
    {
        InitializeComponent();
        _presets = presets;
        Fill(current);
        RefreshPresets();
        Loaded += (_, _) => ExcludeBox.Focus();
    }

    public FilterSettings? Result { get; private set; }

    private FilterSettings Current => new()
    {
        Name = NameBox.Text.Trim(),
        Exclude = ExcludeBox.Text.Trim(),
        Include = IncludeBox.Text.Trim(),
    };

    private void Fill(FilterSettings f)
    {
        ExcludeBox.Text = f.Exclude;
        IncludeBox.Text = f.Include;
        NameBox.Text = f.Name;
    }

    private void RefreshPresets()
    {
        Presets.ItemsSource = null;
        Presets.ItemsSource = _presets.ToList();
    }

    private void OnPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Presets.SelectedItem is FilterSettings preset) Fill(preset);
    }

    private void OnSavePreset(object sender, RoutedEventArgs e)
    {
        var current = Current;
        if (current.Name.Length == 0)
        {
            NameBox.Focus();
            return;
        }

        var index = _presets.ToList().FindIndex(p => p.Name.Equals(current.Name, StringComparison.CurrentCultureIgnoreCase));
        if (index >= 0) _presets[index] = current;
        else _presets.Add(current);
        RefreshPresets();
        Presets.SelectedIndex = _presets.IndexOf(current);
    }

    private void OnDeletePreset(object sender, RoutedEventArgs e)
    {
        if (Presets.SelectedItem is not FilterSettings preset) return;
        _presets.Remove(preset);
        RefreshPresets();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Result = FilterSettings.None;
        DialogResult = true;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        Result = Current;
        DialogResult = true;
    }
}

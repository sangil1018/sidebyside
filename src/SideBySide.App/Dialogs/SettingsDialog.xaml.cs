using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SideBySide.App.Commands;
using SideBySide.App.Modules;
using SideBySide.App.Services;
using SideBySide.App.ViewModels;
using SideBySide.Core.Settings;
using SideBySide.Core.Theming;
using SideBySide.Core.Localization;

namespace SideBySide.App.Dialogs;

public partial class SettingsDialog : Window
{
    /// <summary>단축키 탭의 한 행.</summary>
    public sealed partial class ShortcutRow : ObservableObject
    {
        public required string Id { get; init; }

        public required string Title { get; init; }

        public required CommandScope Scope { get; init; }

        public string ScopeText => Scope switch
        {
            CommandScope.FileList => L.T("파일 목록"),
            CommandScope.Diff => L.T("파일 비교"),
            _ => L.T("전체"),
        };

        [ObservableProperty]
        private string _gesture = string.Empty;
    }

    private readonly Dictionary<string, string> _bindings;
    private readonly List<ShortcutRow> _rows;
    private readonly Dictionary<SystemPlace, CheckBox> _placeBoxes = [];
    private readonly Dictionary<string, (CheckBox Enabled, ComboBox Mode, ModulePreference Original)> _moduleRows = [];
    private readonly List<RadioButton> _swatches = [];
    private readonly Action<string>? _previewAccent;
    private string _accent;
    private bool _syncingAccent;
    private Key _pendingKey = Key.None;
    private ModifierKeys _pendingModifiers;

    /// <param name="previewAccent">포인트 색을 고를 때마다 바로 적용해 보여 주는 콜백 (취소 시 되돌리는 것은 호출자 몫).</param>
    public SettingsDialog(AppPreferences current, Action<string>? previewAccent = null)
    {
        _previewAccent = previewAccent;
        _accent = current.AccentColor;
        InitializeComponent();
        (current.Theme switch
        {
            ThemeMode.Light => ThemeLight,
            ThemeMode.Dark => ThemeDark,
            _ => ThemeSystem,
        }).IsChecked = true;
        (current.Language == AppLanguage.English ? LanguageEnglish : LanguageKorean).IsChecked = true;
        DefaultManager.IsChecked = current.DefaultFileManager;
        ShortcutBox.SelectedIndex = current.OpenShortcut switch
        {
            OpenShortcut.WinCtrlE => 1,
            OpenShortcut.WinShiftE => 2,
            _ => 0,
        };
        ShortcutWhenClosed.IsChecked = current.ShortcutWhenClosed;

        foreach (var (place, name, _, glyph) in SidebarViewModel.SystemPlaces)
        {
            if (SystemPlacesPanel.Children.Count > 0) SystemPlacesPanel.Children.Add(new Border { Style = (Style)FindResource("Settings.Divider") });
            var box = new CheckBox { IsChecked = current.SystemPlaces.Contains(place), VerticalAlignment = VerticalAlignment.Center };
            var row = new Grid { Style = (Style)FindResource("Settings.Row") };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), Margin = new Thickness(0, 0, 12, 0) };
            var label = new TextBlock { Text = name, Style = (Style)FindResource("Settings.RowTitle"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            Grid.SetColumn(box, 2);
            row.Children.Add(icon);
            row.Children.Add(label);
            row.Children.Add(box);
            _placeBoxes[place] = box;
            SystemPlacesPanel.Children.Add(row);
        }

        BuildAccentSwatches();
        BuildModuleRows(current.Modules);

        _bindings = new Dictionary<string, string>(current.KeyBindings);
        _rows = CommandRegistry.Defaults
            .Where(d => !d.IsAlternate && !d.IsFixed)
            .Select(d => new ShortcutRow { Id = d.Id, Title = d.Title, Scope = d.Scope })
            .ToList();
        ShortcutList.ItemsSource = _rows;
        RefreshRows();
    }

    public AppPreferences? Result { get; private set; }

    private ShortcutRow? SelectedRow => ShortcutList.SelectedItem as ShortcutRow;

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (GeneralPage is null) return; // InitializeComponent 중
        GeneralPage.Visibility = TabGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SidebarPage.Visibility = TabSidebar.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ModulesPage.Visibility = TabModules.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPage.Visibility = TabShortcuts.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 포인트 색 ───────────────────────────────────────────────────

    private void BuildAccentSwatches()
    {
        AddSwatch(AccentPalette.Default, L.T("기본"), new Rgb(0x2F, 0x6D, 0xB5), null);
        if (ThemeManager.ResolveAccent(AccentPalette.System) is { } system)
            AddSwatch(AccentPalette.System, L.T("Windows 강조색"), system, SideBySide.Modules.Glyphs.Settings);
        foreach (var (name, color) in AccentPalette.Swatches) AddSwatch(color.ToString(), L.T(name), color, null);

        SyncAccentControls();
    }

    private void AddSwatch(string value, string name, Rgb color, string? glyph)
    {
        var swatch = new RadioButton
        {
            Style = (Style)FindResource("Settings.Swatch"),
            GroupName = "Accent",
            Tag = value,
            ToolTip = name,
            Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
        };
        if (glyph is not null)
        {
            var text = AccentPalette.ContrastRatio(color, new Rgb(0xFF, 0xFF, 0xFF)) >= 3 ? Brushes.White : Brushes.Black;
            swatch.Content = new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 11, Foreground = text };
        }

        swatch.Checked += (_, _) =>
        {
            if (_syncingAccent) return;
            SetAccent(value);
        };
        _swatches.Add(swatch);
        AccentSwatches.Children.Add(swatch);
    }

    private void OnAccentHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingAccent || AccentPreview is null) return; // InitializeComponent 중
        if (Rgb.TryParse(AccentHex.Text, out var color)) SetAccent(color.ToString());
    }

    private void SetAccent(string value)
    {
        _accent = value;
        SyncAccentControls();
        _previewAccent?.Invoke(value);
    }

    /// <summary>견본 선택·사용자 지정 입력란·미리보기 원을 현재 값에 맞춘다.</summary>
    private void SyncAccentControls()
    {
        _syncingAccent = true;
        try
        {
            foreach (var swatch in _swatches) swatch.IsChecked = string.Equals((string)swatch.Tag, _accent, StringComparison.OrdinalIgnoreCase);
            if (Rgb.TryParse(_accent, out var custom) && !AccentHex.IsKeyboardFocusWithin) AccentHex.Text = custom.ToString();
            AccentPreview.Background = ThemeManager.ResolveAccent(_accent) is { } c
                ? new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B))
                : Brushes.Transparent;
        }
        finally
        {
            _syncingAccent = false;
        }
    }

    // ── 모듈 ────────────────────────────────────────────────────────

    private void BuildModuleRows(IReadOnlyDictionary<string, ModulePreference> current)
    {
        foreach (var module in ModuleCatalog.All)
        {
            var original = current.TryGetValue(module.Id, out var p) ? p : new ModulePreference(false, ModuleOpenMode.Default);
            if (ModulesPanel.Children.Count > 0) ModulesPanel.Children.Add(new Border { Style = (Style)FindResource("Settings.Divider") });

            var row = new Grid { Style = (Style)FindResource("Settings.Row") };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new TextBlock { Text = module.Glyph, Style = (Style)FindResource("Glyph"), Margin = new Thickness(0, 2, 14, 0), VerticalAlignment = VerticalAlignment.Top };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            text.Children.Add(new TextBlock { Text = module.Title, Style = (Style)FindResource("Settings.RowTitle") });
            text.Children.Add(new TextBlock { Text = module.Description, Style = (Style)FindResource("Settings.RowHint") });
            if (module.UsesElevation)
            {
                var admin = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                admin.Children.Add(new TextBlock { Text = SideBySide.Modules.Glyphs.Shield, Style = (Style)FindResource("Glyph"), FontSize = 11, Margin = new Thickness(0, 0, 4, 0) });
                admin.Children.Add(new TextBlock { Text = L.T("일부 작업은 관리자 권한(UAC) 확인이 필요합니다."), Style = (Style)FindResource("Text.Secondary") });
                text.Children.Add(admin);
            }

            var effective = original.OpenMode == ModuleOpenMode.Default ? module.DefaultOpenMode : original.OpenMode;
            var mode = new ComboBox { Width = 120, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), ToolTip = L.T("여는 곳") };
            mode.Items.Add(new ComboBoxItem { Content = L.T("오른쪽 패널"), Tag = ModuleOpenMode.Panel });
            mode.Items.Add(new ComboBoxItem { Content = L.T("별도 창"), Tag = ModuleOpenMode.Window });
            mode.SelectedIndex = effective == ModuleOpenMode.Window ? 1 : 0;

            var enabled = new CheckBox { IsChecked = original.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = L.T("사용") };
            mode.IsEnabled = original.Enabled;
            enabled.Checked += (_, _) => mode.IsEnabled = true;
            enabled.Unchecked += (_, _) => mode.IsEnabled = false;

            Grid.SetColumn(text, 1);
            Grid.SetColumn(mode, 2);
            Grid.SetColumn(enabled, 3);
            row.Children.Add(icon);
            row.Children.Add(text);
            row.Children.Add(mode);
            row.Children.Add(enabled);
            _moduleRows[module.Id] = (enabled, mode, original);
            ModulesPanel.Children.Add(row);
        }
    }

    private Dictionary<string, ModulePreference> ModuleResult()
    {
        var result = new Dictionary<string, ModulePreference>();
        foreach (var (id, (enabled, mode, original)) in _moduleRows)
        {
            var chosen = (ModuleOpenMode)((ComboBoxItem)mode.SelectedItem).Tag;
            var descriptor = ModuleCatalog.Find(id)!;

            // 기본값을 그대로 둔 모듈은 '기본'으로 남겨 두어 모듈 기본값을 따라가게 한다
            var stored = original.OpenMode == ModuleOpenMode.Default && chosen == descriptor.DefaultOpenMode ? ModuleOpenMode.Default : chosen;
            result[id] = new ModulePreference(enabled.IsChecked == true, stored);
        }

        return result;
    }

    // ── 단축키 ──────────────────────────────────────────────────────

    /// <summary>각 행에 현재 편집 중인 단축키(기본 + 보조)를 표시한다.</summary>
    private void RefreshRows()
    {
        var composed = CommandRegistry.Compose(_bindings);
        foreach (var row in _rows)
        {
            var gestures = composed.Where(d => d.Id == row.Id && d.Key != Key.None).Select(d => d.Gesture).ToList();
            row.Gesture = gestures.Count > 0 ? string.Join(", ", gestures) : L.T("(없음)");
        }
    }

    private void OnShortcutSelected(object sender, SelectionChangedEventArgs e)
    {
        _pendingKey = Key.None;
        CaptureBox.Text = string.Empty;
    }

    private void OnCaptureKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return; // 수정 키만 누른 상태

        _pendingKey = key;
        _pendingModifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt);
        CaptureBox.Text = CommandDefinition.FormatGesture(_pendingKey, _pendingModifiers);
    }

    private void OnAssign(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            ShortcutHint.Text = L.T("먼저 목록에서 명령을 고르세요.");
            return;
        }

        if (_pendingKey == Key.None)
        {
            ShortcutHint.Text = L.T("입력란을 누른 뒤 원하는 키 조합을 누르세요.");
            return;
        }

        if (!GestureCodec.IsValid(_pendingKey, _pendingModifiers))
        {
            ShortcutHint.Text = L.T("이 조합은 쓸 수 없습니다. 글자·숫자 키는 Ctrl 또는 Alt와 함께 누르세요.");
            return;
        }

        var candidate = GestureCodec.Format(_pendingKey, _pendingModifiers);
        var trial = new Dictionary<string, string>(_bindings) { [row.Id] = candidate };
        var conflicts = CommandRegistry.Compose(trial)
            .Where(d => d.Id != row.Id && d.Key == _pendingKey && d.Modifiers == _pendingModifiers && CommandRegistry.ScopesOverlap(d.Scope, row.Scope))
            .ToList();

        if (conflicts.Count > 0)
        {
            var names = string.Join(", ", conflicts.Select(d => $"'{d.Title}'").Distinct());
            var answer = MessageBox.Show(this,
                L.F("{0}은(는) {1}에서 이미 쓰고 있습니다.\n그 명령에서 이 단축키를 빼고 여기에 지정할까요?", CaptureBox.Text, names),
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            foreach (var other in conflicts.DistinctBy(d => d.Id)) RemoveGesture(trial, other.Id, _pendingKey, _pendingModifiers);
        }

        // 기본값과 같으면 덮어쓰기를 지워 보조 단축키도 되살린다
        var def = CommandRegistry.DefaultOf(row.Id);
        if (def.Key == _pendingKey && def.Modifiers == _pendingModifiers) trial.Remove(row.Id);

        Replace(trial);
        ShortcutHint.Text = $"'{row.Title}' → {CaptureBox.Text}";
    }

    /// <summary>다른 명령에서 겹치는 키만 뺀다: 남은 키 중 하나를 대표로 남기고, 없으면 단축키 없음.</summary>
    private static void RemoveGesture(Dictionary<string, string> bindings, string id, Key key, ModifierKeys modifiers)
    {
        var remaining = CommandRegistry.Compose(bindings)
            .Where(d => d.Id == id && d.Key != Key.None && !(d.Key == key && d.Modifiers == modifiers))
            .ToList();
        bindings[id] = remaining.Count > 0 ? GestureCodec.Format(remaining[0].Key, remaining[0].Modifiers) : string.Empty;
    }

    private void OnClearOne(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row) return;
        _bindings[row.Id] = string.Empty;
        RefreshRows();
    }

    private void OnResetOne(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row) return;
        _bindings.Remove(row.Id);
        RefreshRows();
    }

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        _bindings.Clear();
        RefreshRows();
    }

    private void Replace(Dictionary<string, string> bindings)
    {
        _bindings.Clear();
        foreach (var (id, value) in bindings) _bindings[id] = value;
        RefreshRows();
    }

    // ── 확인 ────────────────────────────────────────────────────────

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var theme = ThemeDark.IsChecked == true ? ThemeMode.Dark
            : ThemeLight.IsChecked == true ? ThemeMode.Light
            : ThemeMode.System;
        var shortcut = ShortcutBox.SelectedIndex switch
        {
            1 => OpenShortcut.WinCtrlE,
            2 => OpenShortcut.WinShiftE,
            _ => OpenShortcut.WinAltE,
        };
        var places = _placeBoxes.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToList();
        Result = new AppPreferences(
            theme,
            LanguageEnglish.IsChecked == true ? AppLanguage.English : AppLanguage.Korean,
            DefaultManager.IsChecked == true,
            shortcut,
            ShortcutWhenClosed.IsChecked == true,
            places,
            new Dictionary<string, string>(_bindings),
            _accent,
            ModuleResult());
        DialogResult = true;
    }
}

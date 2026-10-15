using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using SideBySide.App.Commands;
using SideBySide.App.Services;
using SideBySide.App.ViewModels;
using SideBySide.Core.Settings;
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
    private Key _pendingKey = Key.None;
    private ModifierKeys _pendingModifiers;

    public SettingsDialog(AppPreferences current)
    {
        InitializeComponent();
        (current.Theme switch
        {
            ThemeMode.Light => ThemeLight,
            ThemeMode.Dark => ThemeDark,
            _ => ThemeSystem,
        }).IsChecked = true;
        (current.Language == AppLanguage.English ? LanguageEnglish : LanguageKorean).IsChecked = true;
        DefaultManager.IsChecked = current.DefaultFileManager;
        (current.OpenShortcut switch
        {
            OpenShortcut.WinCtrlE => ShortcutWinCtrlE,
            OpenShortcut.WinShiftE => ShortcutWinShiftE,
            _ => ShortcutWinAltE,
        }).IsChecked = true;
        ShortcutWhenClosed.IsChecked = current.ShortcutWhenClosed;

        foreach (var (place, name, _, _) in SidebarViewModel.SystemPlaces)
        {
            var box = new CheckBox { Content = name, IsChecked = current.SystemPlaces.Contains(place), Margin = new Thickness(0, 0, 0, 8) };
            _placeBoxes[place] = box;
            SystemPlacesPanel.Children.Add(box);
        }

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
        ShortcutsPage.Visibility = TabShortcuts.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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
        var shortcut = ShortcutWinCtrlE.IsChecked == true ? OpenShortcut.WinCtrlE
            : ShortcutWinShiftE.IsChecked == true ? OpenShortcut.WinShiftE
            : OpenShortcut.WinAltE;
        var places = _placeBoxes.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToList();
        Result = new AppPreferences(
            theme,
            LanguageEnglish.IsChecked == true ? AppLanguage.English : AppLanguage.Korean,
            DefaultManager.IsChecked == true,
            shortcut,
            ShortcutWhenClosed.IsChecked == true,
            places,
            new Dictionary<string, string>(_bindings));
        DialogResult = true;
    }
}

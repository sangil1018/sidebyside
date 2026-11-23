using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using SideBySide.App.ViewModels;
using SideBySide.Core.Localization;

namespace SideBySide.App.Views;

/// <summary>주소 표시줄. DataContext = <see cref="TabViewModel"/>.</summary>
public partial class AddressBar : UserControl
{
    private TabViewModel? _tab;

    public AddressBar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as TabViewModel);
    }

    public void BeginEdit()
    {
        if (_tab is null) return;
        Editor.Text = _tab.IsThisPc ? L.T("내 PC") : _tab.Path;
        Editor.Visibility = Visibility.Visible;
        CrumbScroll.Visibility = Visibility.Hidden;
        Editor.Focus();
        Editor.SelectAll();
    }

    private void EndEdit()
    {
        Editor.Visibility = Visibility.Collapsed;
        CrumbScroll.Visibility = Visibility.Visible;
    }

    private void Attach(TabViewModel? tab)
    {
        if (_tab is not null) _tab.PropertyChanged -= OnTabPropertyChanged;
        _tab = tab;
        if (_tab is not null) _tab.PropertyChanged += OnTabPropertyChanged;
        Rebuild();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.Path)) Rebuild();
    }

    private void Rebuild()
    {
        EndEdit();
        var segments = new List<Crumb> { new(L.T("내 PC"), TabViewModel.ThisPc, false) };
        var path = _tab?.Path ?? string.Empty;
        if (path.Length > 0)
        {
            var root = Path.GetPathRoot(path) ?? path;
            segments.Add(new Crumb(root.TrimEnd('\\'), root, true));
            var current = root;
            foreach (var part in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                segments.Add(new Crumb(part, current, true));
            }
        }

        Crumbs.ItemsSource = segments;
        Dispatcher.BeginInvoke(() => CrumbScroll.ScrollToRightEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnCrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && _tab is not null) _ = _tab.NavigateAsync(path);
    }

    private void OnFrameMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Border or ScrollViewer or StackPanel or ItemsPresenter) BeginEdit();
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                var target = Editor.Text;
                EndEdit();
                if (_tab is not null) _ = _tab.NavigateAsync(target);
                MoveFocusToList();
                e.Handled = true;
                break;
            case Key.Escape:
                EndEdit();
                MoveFocusToList();
                e.Handled = true;
                break;
        }
    }

    private void OnEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndEdit();

    private void MoveFocusToList()
    {
        if (Infrastructure.VisualTree.FindAncestor<FilePane>(this) is { } pane) pane.FocusList();
    }

    public sealed record Crumb(string Name, string Path, bool ShowSeparator);

    /// <summary>
    /// UI 자동화용: 주소 표시줄을 값(경로) 하나짜리 컨트롤로 보여 준다. 드라이버가 포커스·키 입력 없이
    /// <c>ValuePattern.SetValue(경로)</c>로 이동할 수 있다 (사용자가 Enter로 이동하는 것과 같은 경로).
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new AddressBarAutomationPeer(this);

    private sealed class AddressBarAutomationPeer(AddressBar owner) : UserControlAutomationPeer(owner), IValueProvider
    {
        public string Value => owner._tab?.Path ?? string.Empty;

        public bool IsReadOnly => owner._tab is null;

        public void SetValue(string value)
        {
            if (owner._tab is not { } tab) throw new ElementNotEnabledException();
            owner.EndEdit();
            _ = tab.NavigateAsync(value);
        }

        public override object? GetPattern(PatternInterface patternInterface)
            => patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;

        protected override string GetClassNameCore() => nameof(AddressBar);
    }
}

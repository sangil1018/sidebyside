using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SideBySide.App.Services;
using SideBySide.Core.Settings;

namespace SideBySide.App.ViewModels;

public enum PaneSide
{
    Left,
    Right,
}

/// <summary>좌/우 창 하나: 탭 목록과 활성 탭.</summary>
public sealed partial class PaneViewModel : ObservableObject
{
    private readonly ExplorerContext _ctx;

    [ObservableProperty]
    private TabViewModel _activeTab;

    [ObservableProperty]
    private bool _isActive;

    public PaneViewModel(ExplorerContext ctx, PaneSide side, PaneState state)
    {
        _ctx = ctx;
        Side = side;
        var paths = state.Tabs.Count > 0 ? state.Tabs : [TabViewModel.ThisPc];
        foreach (var path in paths) Tabs.Add(CreateTab(path));
        _activeTab = Tabs[Math.Clamp(state.ActiveTab, 0, Tabs.Count - 1)];
        _activeTab.SelectionChanged += OnTabSelectionChanged;
        _activeTab.IsActive = true;
    }

    public PaneSide Side { get; }

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    /// <summary>활성 탭이 바뀌거나 활성 탭의 선택이 바뀌면 발생 (상태 표시줄, 미리보기 갱신용).</summary>
    public event EventHandler? StateChanged;

    public TabViewModel NewTab(string? path = null, bool activate = true)
    {
        var tab = CreateTab(path ?? ActiveTab.Path);
        Tabs.Insert(Tabs.IndexOf(ActiveTab) + 1, tab);
        if (activate) ActiveTab = tab;
        _ = tab.LoadAsync();
        return tab;
    }

    [RelayCommand]
    private void AddTab() => NewTab();

    [RelayCommand]
    public void CloseTab(TabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (Tabs.Count == 1)
        {
            // 마지막 탭은 닫지 않고 내 PC로 이동
            _ = tab.NavigateAsync(TabViewModel.ThisPc);
            return;
        }

        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (ReferenceEquals(ActiveTab, tab)) ActiveTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        tab.Dispose();
    }

    [RelayCommand]
    private void ActivateTab(TabViewModel tab) => ActiveTab = tab;

    [RelayCommand]
    private void CloseOtherTabs(TabViewModel? tab)
    {
        tab ??= ActiveTab;
        foreach (var other in Tabs.Where(t => !ReferenceEquals(t, tab)).ToList())
        {
            Tabs.Remove(other);
            other.Dispose();
        }

        ActiveTab = tab;
    }

    public void CycleTab(int delta)
    {
        var index = (Tabs.IndexOf(ActiveTab) + delta + Tabs.Count) % Tabs.Count;
        ActiveTab = Tabs[index];
    }

    /// <summary>
    /// 두 창의 탭을 통째로 맞바꾼다 (탭 목록 + 활성 탭). 창 자체(<see cref="Side"/>)와 뷰는 그대로다.
    /// </summary>
    public void SwapTabsWith(PaneViewModel other)
    {
        if (ReferenceEquals(this, other)) return;

        var (mine, theirs) = (Tabs.ToList(), other.Tabs.ToList());
        var (myActive, theirActive) = (ActiveTab, other.ActiveTab);

        foreach (var tab in mine) Detach(tab);
        foreach (var tab in theirs) other.Detach(tab);
        Tabs.Clear();
        other.Tabs.Clear();
        foreach (var tab in theirs)
        {
            Attach(tab);
            Tabs.Add(tab);
        }

        foreach (var tab in mine)
        {
            other.Attach(tab);
            other.Tabs.Add(tab);
        }

        ActiveTab = theirActive;
        other.ActiveTab = myActive;
        // 두 번째 대입이 "이전 활성 탭"(= 지금 이 창의 활성 탭)의 표시를 껐으므로 다시 켠다
        theirActive.IsActive = true;
    }

    public PaneState SaveState() => new()
    {
        Tabs = Tabs.Select(t => t.Path).ToList(),
        ActiveTab = Tabs.IndexOf(ActiveTab),
    };

    /// <summary>활성 탭만 읽는다. 나머지 탭은 처음 활성화될 때 읽어 시작 속도를 높인다.</summary>
    public Task LoadActiveAsync() => ActiveTab.LoadAsync();

    partial void OnActiveTabChanged(TabViewModel? oldValue, TabViewModel newValue)
    {
        if (oldValue is not null)
        {
            oldValue.SelectionChanged -= OnTabSelectionChanged;
            oldValue.IsActive = false;
        }

        newValue.SelectionChanged += OnTabSelectionChanged;
        newValue.IsActive = true;
        if (!newValue.HasLoaded && !newValue.IsCompareView) _ = newValue.LoadAsync();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private TabViewModel CreateTab(string path)
    {
        var tab = new TabViewModel(_ctx, path);
        Attach(tab);
        return tab;
    }

    // 탭이 다른 창으로 옮겨 갈 수 있으므로(SwapTabsWith) 구독을 걸고 풀 수 있게 이름 있는 처리기로 둔다
    private void Attach(TabViewModel tab)
    {
        tab.PropertyChanged += OnTabPropertyChanged;
        tab.Items.CollectionChanged += OnTabItemsChanged;
    }

    private void Detach(TabViewModel tab)
    {
        tab.PropertyChanged -= OnTabPropertyChanged;
        tab.Items.CollectionChanged -= OnTabItemsChanged;
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ActiveTab) && e.PropertyName is nameof(TabViewModel.Path) or nameof(TabViewModel.HiddenCount) or nameof(TabViewModel.IsSearching))
            StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTabItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ActiveTab.Items)) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTabSelectionChanged(object? sender, EventArgs e) => StateChanged?.Invoke(this, EventArgs.Empty);
}

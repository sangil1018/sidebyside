using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SideBySide.App.Infrastructure;
using SideBySide.App.ViewModels;

namespace SideBySide.App.Views;

/// <summary>왼쪽 탐색 창. 클릭 = 활성 창에서 열기, 가운데 클릭 = 새 탭, 오른쪽 클릭 = 메뉴. DataContext = <see cref="MainViewModel"/>.</summary>
public partial class NavigationSidebar : UserControl
{
    public NavigationSidebar() => InitializeComponent();

    private MainViewModel? Main => DataContext as MainViewModel;

    private void OnEjectClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SidebarItem item } && Main is { } main) _ = main.EjectAsync(item.Path);
        e.Handled = true;
    }

    private void OnItemMouseUp(object sender, MouseButtonEventArgs e)
    {
        // 꺼내기 버튼 클릭은 항목 열기로 처리하지 않는다
        if (VisualTree.FindAncestor<Button>(e.OriginalSource) is not null) return;
        if (Main is null || VisualTree.FindAncestor<ListBoxItem>(e.OriginalSource)?.DataContext is not SidebarItem item) return;

        switch (e.ChangedButton)
        {
            case MouseButton.Left:
                Main.NavigateActive(item.Path);
                break;
            case MouseButton.Middle:
                Main.ActivePane.NewTab(item.Path);
                break;
            case MouseButton.Right:
                ShowMenu(item);
                break;
        }

        e.Handled = true;
    }

    private void OnThisPcClick(object sender, MouseButtonEventArgs e) => Main?.NavigateActive(TabViewModel.ThisPc);

    private void ShowMenu(SidebarItem item)
    {
        if (Main is not { } main) return;
        var menu = new ContextMenu();
        menu.Items.Add(Item("열기", () => main.NavigateActive(item.Path)));
        menu.Items.Add(Item("새 탭에서 열기", () => main.ActivePane.NewTab(item.Path)));
        if (main.IsRightPaneVisible) menu.Items.Add(Item("반대쪽 창에서 열기", () => _ = main.OtherPane.ActiveTab.NavigateAsync(item.Path)));
        if (item.CanEject)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("꺼내기 (안전하게 제거)", () => _ = main.EjectAsync(item.Path)));
        }

        if (item.IsPinned)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("고정 해제", () => main.Sidebar.Unpin(item.Path)));
        }

        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        return mi;
    }
}

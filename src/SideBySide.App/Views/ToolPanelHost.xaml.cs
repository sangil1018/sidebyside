using System.Windows;
using System.Windows.Controls;
using SideBySide.App.Modules;

namespace SideBySide.App.Views;

/// <summary>오른쪽 도구 패널. 탭 선택 상태는 <see cref="ModuleManager.ActivePanelTool"/>와 맞춘다.</summary>
public partial class ToolPanelHost : UserControl
{
    public ToolPanelHost()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ModuleManager old) old.PropertyChanged -= OnManagerChanged;
            if (e.NewValue is ModuleManager now) now.PropertyChanged += OnManagerChanged;
        };
    }

    private ModuleManager? Manager => DataContext as ModuleManager;

    private void OnManagerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ModuleManager.ActivePanelTool)) return;
        foreach (var tab in Infrastructure.VisualTree.FindDescendants<RadioButton>(Tabs))
            tab.IsChecked = ReferenceEquals(tab.DataContext, Manager?.ActivePanelTool);
    }

    private void OnTabLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton tab) tab.IsChecked = ReferenceEquals(tab.DataContext, Manager?.ActivePanelTool);
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PanelTool tool } && Manager is { } manager) manager.ActivePanelTool = tool;
    }

    private void OnPopOut(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PanelTool tool }) Manager?.PopOut(tool);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PanelTool tool }) Manager?.ClosePanelTool(tool);
    }
}

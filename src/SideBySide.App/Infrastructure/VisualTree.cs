using System.Windows;
using System.Windows.Media;

namespace SideBySide.App.Infrastructure;

public static class VisualTree
{
    /// <summary>시각/논리 트리를 따라 올라가며 T를 찾는다 (Run 같은 ContentElement에서 시작해도 동작).</summary>
    public static T? FindAncestor<T>(object? start, DependencyObject? stopAt = null)
        where T : DependencyObject
    {
        var current = start as DependencyObject;
        while (current is not null && !ReferenceEquals(current, stopAt))
        {
            if (current is T match) return match;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    public static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } found) return found;
        }

        return null;
    }
}

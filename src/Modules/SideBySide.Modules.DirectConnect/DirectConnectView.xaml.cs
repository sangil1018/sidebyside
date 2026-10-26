using System.Windows;
using System.Windows.Controls;

namespace SideBySide.Modules.DirectConnect;

public partial class DirectConnectView : UserControl
{
    public DirectConnectView() => InitializeComponent();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && DataContext is DirectConnectViewModel vm) vm.QueueFiles(files);
    }
}

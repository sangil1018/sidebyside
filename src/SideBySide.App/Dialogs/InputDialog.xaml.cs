using System.Windows;

namespace SideBySide.App.Dialogs;

public partial class InputDialog : Window
{
    public InputDialog(string title, string label, string initialValue)
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        Input.Text = initialValue;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    public string Value => Input.Text;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Input.Text)) return;
        DialogResult = true;
    }
}

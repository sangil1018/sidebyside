using System.Windows.Input;
using System.Windows.Markup;

namespace SideBySide.App.Commands;

/// <summary>XAML: <c>Command="{cmd:Command NewFolder}"</c></summary>
[MarkupExtensionReturnType(typeof(ICommand))]
public sealed class CommandExtension(string id) : MarkupExtension
{
    public string Id { get; set; } = id;

    public override object? ProvideValue(IServiceProvider serviceProvider) => CommandHost.Current?[Id];
}

/// <summary>XAML: <c>ToolTip="{cmd:Tip NewFolder}"</c> → "새 폴더 (Ctrl+Shift+N)"</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TipExtension(string id) : MarkupExtension
{
    public string Id { get; set; } = id;

    public override object ProvideValue(IServiceProvider serviceProvider) => CommandRegistry.Get(Id).ToolTip;
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Markup;

namespace SideBySide.App.Commands;

/// <summary>
/// XAML: <c>Command="{cmd:Command NewFolder}"</c>.
/// 붙는 컨트롤에 UI 자동화 ID(<c>cmd.NewFolder</c>)와 이름(명령 이름)을 함께 단다 — 아이콘만 있는 단추도
/// UIA 드라이버가 마우스 없이 찾아 실행할 수 있게. 이미 지정돼 있으면 건드리지 않는다.
/// </summary>
[MarkupExtensionReturnType(typeof(ICommand))]
public sealed class CommandExtension(string id) : MarkupExtension
{
    public string Id { get; set; } = id;

    public override object? ProvideValue(IServiceProvider serviceProvider)
    {
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget { TargetObject: DependencyObject target })
        {
            if (string.IsNullOrEmpty(AutomationProperties.GetAutomationId(target))) AutomationProperties.SetAutomationId(target, "cmd." + Id);
            if (string.IsNullOrEmpty(AutomationProperties.GetName(target)) && CommandRegistry.TryGet(Id, out var definition))
                AutomationProperties.SetName(target, definition.Title);
        }

        return CommandHost.Current?[Id];
    }
}

/// <summary>XAML: <c>ToolTip="{cmd:Tip NewFolder}"</c> → "새 폴더 (Ctrl+Shift+N)"</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TipExtension(string id) : MarkupExtension
{
    public string Id { get; set; } = id;

    public override object ProvideValue(IServiceProvider serviceProvider) => CommandRegistry.Get(Id).ToolTip;
}

using System.Windows.Markup;
using SideBySide.Core.Localization;

namespace SideBySide.App.Infrastructure;

/// <summary>XAML 번역 문자열: <c>Text="{l:T '새 탭'}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string text) : MarkupExtension
{
    public string Text { get; } = text;

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}

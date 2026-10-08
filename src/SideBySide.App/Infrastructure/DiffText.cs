using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using SideBySide.App.ViewModels;

namespace SideBySide.App.Infrastructure;

/// <summary>
/// 파일 비교 줄의 텍스트를 Run으로 나눠 바뀐 글자만 강조한다.
/// XAML: <c>&lt;TextBlock infra:DiffText.Left="{Binding}" /&gt;</c>
/// </summary>
public static class DiffText
{
    public static readonly DependencyProperty LeftProperty = DependencyProperty.RegisterAttached(
        "Left", typeof(DiffLineViewModel), typeof(DiffText), new PropertyMetadata(null, (d, e) => Build(d, e.NewValue as DiffLineViewModel, left: true)));

    public static readonly DependencyProperty RightProperty = DependencyProperty.RegisterAttached(
        "Right", typeof(DiffLineViewModel), typeof(DiffText), new PropertyMetadata(null, (d, e) => Build(d, e.NewValue as DiffLineViewModel, left: false)));

    public static DiffLineViewModel? GetLeft(DependencyObject d) => (DiffLineViewModel?)d.GetValue(LeftProperty);

    public static void SetLeft(DependencyObject d, DiffLineViewModel? value) => d.SetValue(LeftProperty, value);

    public static DiffLineViewModel? GetRight(DependencyObject d) => (DiffLineViewModel?)d.GetValue(RightProperty);

    public static void SetRight(DependencyObject d, DiffLineViewModel? value) => d.SetValue(RightProperty, value);

    private static void Build(DependencyObject d, DiffLineViewModel? line, bool left)
    {
        if (d is not TextBlock block) return;
        block.Inlines.Clear();
        if (line is null) return;

        var text = left ? line.LeftText : line.RightText;
        var spans = left ? line.LeftSpans : line.RightSpans;
        if (spans.Count == 0)
        {
            block.Inlines.Add(new Run(text));
            return;
        }

        var brushKey = left ? "Brush.DiffCharLeft" : "Brush.DiffCharRight";
        var pos = 0;
        foreach (var span in spans)
        {
            var start = Math.Clamp(span.Start, 0, text.Length);
            var end = Math.Clamp(span.Start + span.Length, start, text.Length);
            if (start > pos) block.Inlines.Add(new Run(text[pos..start]));
            if (end > start)
            {
                var run = new Run(text[start..end]);
                run.SetResourceReference(TextElement.BackgroundProperty, brushKey);
                block.Inlines.Add(run);
            }

            pos = end;
        }

        if (pos < text.Length) block.Inlines.Add(new Run(text[pos..]));
    }
}

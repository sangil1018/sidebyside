using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using SideBySide.Core.Localization;

[assembly: XmlnsDefinition("urn:sidebyside-modules", "SideBySide.Modules")]
[assembly: XmlnsPrefix("urn:sidebyside-modules", "m")]

namespace SideBySide.Modules;

/// <summary>
/// 모든 모듈 화면의 공통 골격: 머리글(아이콘·제목·설명) / 본문(스크롤) / 진행 행 / 아래 고정 버튼 줄.
/// 모양은 앱의 Themes/Controls/ToolPanel.xaml이 정한다 (모듈은 색·스타일을 직접 정하지 않는다).
/// </summary>
public class ToolPanel : ContentControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(ToolPanel));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(ToolPanel));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(ToolPanel));
    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(nameof(Footer), typeof(object), typeof(ToolPanel));
    public static readonly DependencyProperty HeaderToolsProperty = DependencyProperty.Register(nameof(HeaderTools), typeof(object), typeof(ToolPanel));
    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(nameof(IsBusy), typeof(bool), typeof(ToolPanel));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(ToolPanel), new PropertyMetadata(double.NaN));
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(nameof(Status), typeof(string), typeof(ToolPanel));
    public static readonly DependencyProperty ScrollContentProperty = DependencyProperty.Register(nameof(ScrollContent), typeof(bool), typeof(ToolPanel), new PropertyMetadata(true));

    static ToolPanel()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ToolPanel), new FrameworkPropertyMetadata(typeof(ToolPanel)));
    }

    /// <summary>머리글 아이콘 (Segoe Fluent 글리프, <see cref="Glyphs"/>).</summary>
    public string? Glyph { get => (string?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string? Subtitle { get => (string?)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    /// <summary>아래 고정 버튼 줄 (오른쪽 정렬). 주 버튼은 Button.Primary.</summary>
    public object? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }

    /// <summary>머리글 오른쪽의 작은 아이콘 버튼들.</summary>
    public object? HeaderTools { get => GetValue(HeaderToolsProperty); set => SetValue(HeaderToolsProperty, value); }

    /// <summary>작업 중이면 진행 행을 보인다.</summary>
    public bool IsBusy { get => (bool)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }

    /// <summary>0..1. NaN이면 진행률을 알 수 없는 작업 (띠 애니메이션).</summary>
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    /// <summary>진행 행 또는 버튼 줄 왼쪽에 보이는 한 줄 상태.</summary>
    public string? Status { get => (string?)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    /// <summary>false면 본문이 스크롤되지 않고 남은 공간을 모두 쓴다 (목록·뷰어처럼 자체 스크롤이 있는 화면).</summary>
    public bool ScrollContent { get => (bool)GetValue(ScrollContentProperty); set => SetValue(ScrollContentProperty, value); }
}

/// <summary>본문 안의 묶음: 작은 소제목 + 카드 모양 내용.</summary>
public class ToolSection : HeaderedContentControl
{
    static ToolSection()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ToolSection), new FrameworkPropertyMetadata(typeof(ToolSection)));
    }
}

/// <summary>카드 안의 한 행: 왼쪽 이름(과 설명), 오른쪽 입력.</summary>
public class ToolRow : ContentControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(ToolRow));
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(nameof(Hint), typeof(string), typeof(ToolRow));

    static ToolRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ToolRow), new FrameworkPropertyMetadata(typeof(ToolRow)));
    }

    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public string? Hint { get => (string?)GetValue(HintProperty); set => SetValue(HintProperty, value); }
}

/// <summary>모듈 XAML 번역 문자열: <c>Text="{m:T '크기 조정'}"</c> (앱의 <c>{l:T}</c>와 같다).</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string text) : MarkupExtension
{
    public string Text { get; } = text;

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}

/// <summary>
/// 앱 전체가 쓰는 아이콘 글리프 (Segoe Fluent Icons / MDL2). 앱의 Themes/Icons.xaml도 이 값을 쓴다 → 메뉴·패널·설정이 같은 아이콘.
/// </summary>
public static class Glyphs
{
    public const string Tools = "";
    public const string Resize = "";
    public const string Renumber = "";
    public const string SequenceViewer = "";
    public const string Cleaner = "";
    public const string LanShare = "";
    public const string DirectConnect = "";
    public const string Shield = "";
    public const string Folder = "";
    public const string OpenFolder = "";
    public const string Add = "";
    public const string Remove = "";
    public const string Close = "";
    public const string Refresh = "";
    public const string Play = "";
    public const string Pause = "";
    public const string Previous = "";
    public const string Next = "";
    public const string First = "";
    public const string Fit = "";
    public const string Eye = "";
    public const string Hidden = "";
    public const string Up = "";
    public const string Down = "";
    public const string Phone = "";
    public const string Pc = "";
    public const string Send = "";
    public const string Download = "";
    public const string Check = "";
    public const string Warning = "";
    public const string Save = "";
    public const string Settings = "";
    public const string Network = "";
    public const string Window = "";
    public const string Panel = "";
}

using System.Globalization;

namespace SideBySide.Core.Theming;

/// <summary>sRGB 색 (알파 없음).</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>"#RRGGBB" 또는 "RRGGBB". 형식이 틀리면 false.</summary>
    public static bool TryParse(string? text, out Rgb color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }
}

/// <summary>강조색 한 벌: 테마 토큰 중 강조색에서 파생되는 값들.</summary>
/// <param name="Accent">포커스·활성 표시·주 버튼.</param>
/// <param name="AccentText">강조 배경 위 글자 (흑/백 중 대비가 큰 쪽).</param>
/// <param name="AccentSubtle">옅은 강조 배경 (필터 칩 등).</param>
/// <param name="Selected">활성 창의 선택 배경.</param>
public readonly record struct AccentSet(Rgb Accent, Rgb AccentText, Rgb AccentSubtle, Rgb Selected);

/// <summary>
/// 사용자가 고른 기준 색을 현재 라이트/다크 바탕에서 읽기 좋은 강조색 한 벌로 바꾼다.
/// UI에 의존하지 않는 순수 계산이라 테스트할 수 있다.
/// </summary>
public static class AccentPalette
{
    /// <summary>설정 값: 앱 기본 강조색 (테마 토큰 그대로).</summary>
    public const string Default = "Default";

    /// <summary>설정 값: Windows 강조색을 따른다.</summary>
    public const string System = "System";

    /// <summary>설정 창의 견본 (기본·시스템 다음에 표시).</summary>
    public static readonly IReadOnlyList<(string Name, Rgb Color)> Swatches =
    [
        ("파랑", new Rgb(0x00, 0x78, 0xD4)),
        ("청록", new Rgb(0x00, 0x99, 0xA6)),
        ("초록", new Rgb(0x2E, 0x9E, 0x5B)),
        ("노랑", new Rgb(0xC9, 0x9A, 0x06)),
        ("주황", new Rgb(0xE3, 0x76, 0x2B)),
        ("빨강", new Rgb(0xD1, 0x34, 0x38)),
        ("분홍", new Rgb(0xE3, 0x00, 0x8C)),
        ("보라", new Rgb(0x87, 0x64, 0xB8)),
    ];

    private static readonly Rgb Black = new(0, 0, 0);
    private static readonly Rgb White = new(0xFF, 0xFF, 0xFF);

    /// <summary>
    /// 기준 색에서 강조색 한 벌을 만든다. 명도는 바탕에서 구분되도록 보정하고(다크는 밝게, 라이트는 어둡게) 채도·색상은 유지한다.
    /// </summary>
    /// <param name="surface">파일 목록 바탕색 (선택 배경을 섞을 때 기준).</param>
    public static AccentSet Build(Rgb baseColor, bool dark, Rgb surface)
    {
        var (h, s, l) = ToHsl(baseColor);
        var accent = FromHsl(h, s, dark ? Math.Clamp(l, 0.58, 0.74) : Math.Clamp(l, 0.30, 0.48));
        var text = ContrastRatio(accent, White) >= ContrastRatio(accent, Black) ? White : Black;
        return new AccentSet(
            accent,
            text,
            Mix(surface, accent, dark ? 0.16 : 0.12),
            Mix(surface, accent, dark ? 0.24 : 0.18));
    }

    /// <summary><paramref name="from"/>에서 <paramref name="to"/>로 <paramref name="amount"/>(0..1)만큼 섞는다.</summary>
    public static Rgb Mix(Rgb from, Rgb to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return new Rgb(Lerp(from.R, to.R), Lerp(from.G, to.G), Lerp(from.B, to.B));

        byte Lerp(byte a, byte b) => (byte)Math.Round(a + ((b - a) * amount));
    }

    /// <summary>WCAG 2 상대 휘도 (0..1).</summary>
    public static double Luminance(Rgb c)
    {
        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

        static double Channel(byte v)
        {
            var x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
    }

    /// <summary>WCAG 2 대비 (1..21).</summary>
    public static double ContrastRatio(Rgb a, Rgb b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static (double H, double S, double L) ToHsl(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min) return (0, 0, l);

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = ((g - b) / d) + (g < b ? 6 : 0);
        else if (max == g) h = ((b - r) / d) + 2;
        else h = ((r - g) / d) + 4;
        return (h / 6, s, l);
    }

    public static Rgb FromHsl(double h, double s, double l)
    {
        if (s <= 0)
        {
            var v = (byte)Math.Round(l * 255);
            return new Rgb(v, v, v);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        var p = (2 * l) - q;
        return new Rgb(Channel(h + (1.0 / 3)), Channel(h), Channel(h - (1.0 / 3)));

        byte Channel(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            double x;
            if (t < 1.0 / 6) x = p + ((q - p) * 6 * t);
            else if (t < 1.0 / 2) x = q;
            else if (t < 2.0 / 3) x = p + ((q - p) * ((2.0 / 3) - t) * 6);
            else x = p;
            return (byte)Math.Round(Math.Clamp(x, 0, 1) * 255);
        }
    }
}

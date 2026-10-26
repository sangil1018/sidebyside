using SideBySide.Core.Theming;

namespace SideBySide.Core.Tests;

public class AccentPaletteTests
{
    private static readonly Rgb DarkSurface = new(0x27, 0x27, 0x27);
    private static readonly Rgb LightSurface = new(0xFF, 0xFF, 0xFF);

    [Theory]
    [InlineData("#0078D4", true)]
    [InlineData("0078d4", true)]
    [InlineData(" #ABCDEF ", true)]
    [InlineData("#12345", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData("System", false)]
    [InlineData(null, false)]
    public void Parses_hex(string? text, bool ok) => Assert.Equal(ok, Rgb.TryParse(text, out _));

    [Fact]
    public void Round_trips_hex()
    {
        Assert.True(Rgb.TryParse("#0078D4", out var c));
        Assert.Equal("#0078D4", c.ToString());
    }

    [Theory]
    [InlineData("#0078D4")]
    [InlineData("#C99A06")]
    [InlineData("#111111")]
    [InlineData("#F0F0F0")]
    [InlineData("#E3008C")]
    public void Accent_is_readable_on_both_themes(string hex)
    {
        Rgb.TryParse(hex, out var color);
        foreach (var (dark, surface) in new[] { (true, DarkSurface), (false, LightSurface) })
        {
            var set = AccentPalette.Build(color, dark, surface);

            // 강조색이 바탕에서 구분되고, 강조 배경 위 글자가 읽힌다
            Assert.True(AccentPalette.ContrastRatio(set.Accent, surface) >= 2.0, $"{hex} dark={dark}");
            Assert.True(AccentPalette.ContrastRatio(set.Accent, set.AccentText) >= 3.0, $"{hex} dark={dark}");

            // 선택 배경은 바탕과 강조색 사이
            Assert.True(AccentPalette.ContrastRatio(set.Selected, surface) < AccentPalette.ContrastRatio(set.Accent, surface));
        }
    }

    [Fact]
    public void Keeps_hue()
    {
        var (h, _, _) = AccentPalette.ToHsl(new Rgb(0xD1, 0x34, 0x38));
        var set = AccentPalette.Build(new Rgb(0xD1, 0x34, 0x38), dark: true, DarkSurface);
        var (h2, _, _) = AccentPalette.ToHsl(set.Accent);
        Assert.InRange(Math.Abs(h - h2), 0, 0.02);
    }

    [Fact]
    public void Hsl_round_trip()
    {
        foreach (var (_, color) in AccentPalette.Swatches)
        {
            var (h, s, l) = AccentPalette.ToHsl(color);
            var back = AccentPalette.FromHsl(h, s, l);
            Assert.InRange(Math.Abs(back.R - color.R), 0, 1);
            Assert.InRange(Math.Abs(back.G - color.G), 0, 1);
            Assert.InRange(Math.Abs(back.B - color.B), 0, 1);
        }
    }
}

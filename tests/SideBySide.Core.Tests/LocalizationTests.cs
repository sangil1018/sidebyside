using System.Text.RegularExpressions;
using SideBySide.Core.Localization;
using Xunit;

namespace SideBySide.Core.Tests;

public sealed partial class LocalizationTests
{
    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex Placeholder();

    [Fact]
    public void EnglishKeepsEveryPlaceholder()
    {
        foreach (var (korean, english) in L.EnglishTable)
        {
            var expected = Placeholder().Matches(korean).Select(m => m.Value).Order().ToList();
            var actual = Placeholder().Matches(english).Select(m => m.Value).Order().ToList();
            Assert.True(expected.SequenceEqual(actual), $"{korean} → {english}");
        }
    }

    [Fact]
    public void EnglishFormatsWithoutErrors()
    {
        var args = Enumerable.Range(0, 5).Select(i => (object)i).ToArray();
        foreach (var english in L.EnglishTable.Values) _ = string.Format(english, args);
    }

    [Fact]
    public void TranslatesAndFallsBackToKorean()
    {
        Assert.Equal("Recycle Bin", L.Translate("휴지통", english: true));
        Assert.Equal("휴지통", L.Translate("휴지통", english: false));
        Assert.Equal("표에 없는 문장", L.Translate("표에 없는 문장", english: true));
    }
}

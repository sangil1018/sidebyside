using SideBySide.Core.Filtering;

namespace SideBySide.Core.Tests;

public class FilterSetTests
{
    [Theory]
    [InlineData("a.tmp", false, false)]
    [InlineData("A.TMP", false, false)]
    [InlineData("a.txt", false, true)]
    [InlineData("bin", true, false)]
    [InlineData("bin", false, true)] // 폴더 전용 패턴은 파일에 적용 안 됨
    [InlineData("node_modules", true, false)]
    [InlineData("log1.log", false, false)]
    public void Exclude_patterns(string name, bool isDir, bool visible)
    {
        var f = FilterSet.Create(new FilterSettings { Exclude = "*.tmp; bin\\\n node_modules/ ;log?.log" }, showHidden: true);
        Assert.Equal(visible, f.IsVisible(name, isDir, FileAttributes.Normal));
    }

    [Fact]
    public void Include_applies_to_files_only()
    {
        var f = FilterSet.Create(new FilterSettings { Include = "*.cs;*.xaml" }, true);
        Assert.True(f.IsVisible("a.cs", false, FileAttributes.Normal));
        Assert.False(f.IsVisible("a.txt", false, FileAttributes.Normal));
        Assert.True(f.IsVisible("folder", true, FileAttributes.Directory));
    }

    [Fact]
    public void Hidden_and_system_respect_setting()
    {
        var hidden = FilterSet.Create(null, showHidden: false);
        Assert.False(hidden.IsVisible("x", false, FileAttributes.Hidden));
        Assert.False(hidden.IsVisible("x", false, FileAttributes.System));
        Assert.True(FilterSet.Create(null, showHidden: true).IsVisible("x", false, FileAttributes.Hidden));
    }

    [Fact]
    public void Regex_special_chars_are_literal()
    {
        var f = FilterSet.Create(new FilterSettings { Exclude = "a+b(1).txt" }, true);
        Assert.False(f.IsVisible("a+b(1).txt", false, FileAttributes.Normal));
        Assert.True(f.IsVisible("aab1.txt", false, FileAttributes.Normal));
        Assert.True(f.HasPatterns);
        Assert.False(FilterSet.Create(null, true).HasPatterns);
    }
}

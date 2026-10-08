using SideBySide.Core.FileSystem;

namespace SideBySide.Core.Tests;

public class NaturalStringComparerTests
{
    [Fact]
    public void Sorts_like_explorer()
    {
        var input = new[] { "file10.txt", "File2.txt", "file1.txt", "file02.txt", "a", "B" };
        var sorted = input.Order(NaturalStringComparer.Instance).ToArray();
        Assert.Equal(["a", "B", "file1.txt", "File2.txt", "file02.txt", "file10.txt"], sorted);
    }

    [Theory]
    [InlineData("ok.txt", true)]
    [InlineData("bad?.txt", false)]
    [InlineData("CON", false)]
    [InlineData("trailing.", false)]
    [InlineData("", false)]
    public void Validate_names(string name, bool valid) => Assert.Equal(valid, PathNames.Validate(name) is null);
}

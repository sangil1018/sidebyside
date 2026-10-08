using SideBySide.Core.Rename;

namespace SideBySide.Core.Tests;

public class BatchRenamerTests
{
    [Fact]
    public void Template_numbering_and_find_replace()
    {
        using var d = new TempDir();
        var a = d.File("photo_a.JPG", "1");
        var b = d.File("photo_b.JPG", "2");

        var preview = BatchRenamer.Preview([a, b], new RenameOptions { Find = "photo_", Replace = "", Template = "{n:3}_{name}{ext}", StartNumber = 9 });
        Assert.Equal(["009_a.JPG", "010_b.JPG"], preview.Select(p => p.NewName));
        Assert.All(preview, p => Assert.Null(p.Error));
    }

    [Fact]
    public void Regex_and_errors()
    {
        using var d = new TempDir();
        var a = d.File("a1.txt", "1");
        var b = d.File("a2.txt", "2");
        d.File("taken.txt", "x");

        var dup = BatchRenamer.Preview([a, b], new RenameOptions { Find = @"\d", Replace = "", UseRegex = true });
        Assert.All(dup, p => Assert.NotNull(p.Error)); // 둘 다 a.txt

        var taken = BatchRenamer.Preview([a], new RenameOptions { Template = "taken{ext}" });
        Assert.NotNull(taken[0].Error);

        var invalid = BatchRenamer.Preview([a], new RenameOptions { Template = "a:b{ext}" });
        Assert.NotNull(invalid[0].Error);

        var badRegex = BatchRenamer.Preview([a], new RenameOptions { Find = "(", UseRegex = true });
        Assert.StartsWith("정규식", badRegex[0].Error);
    }

    [Fact]
    public void Apply_handles_swaps()
    {
        using var d = new TempDir();
        var a = d.File("a.txt", "A");
        var b = d.File("b.txt", "B");

        // a ↔ b 맞바꾸기: {n}으로 1→b, 2→a 를 만들 수 없으므로 직접 미리보기를 구성
        var items = new List<RenamePreview> { new(a, "b.txt", null), new(b, "a.txt", null) };
        BatchRenamer.Apply(items);

        Assert.Equal("B", File.ReadAllText(a));
        Assert.Equal("A", File.ReadAllText(b));
    }
}

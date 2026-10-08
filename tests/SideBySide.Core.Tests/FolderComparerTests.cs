using SideBySide.Core.Compare;
using SideBySide.Core.Filtering;

namespace SideBySide.Core.Tests;

public class FolderComparerTests
{
    private static readonly FilterSet NoFilter = FilterSet.Create(null, showHidden: true);

    [Fact]
    public async Task Level_compare_aligns_and_classifies()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        l.File("same.txt", "hello");
        r.File("same.txt", "hello");
        l.File("diff.txt", "aaa");
        r.File("diff.txt", "bbbb");
        l.File("left.txt", "x");
        r.File("right.txt", "x");
        l.File("newer.txt", "abc", new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        r.File("newer.txt", "abc");

        var rows = await new FolderComparer().CompareLevelAsync(l.Path, r.Path, NoFilter, new CompareOptions());
        var byName = rows.ToDictionary(x => x.Name);

        Assert.Equal(CompareState.Same, byName["same.txt"].State);
        Assert.Equal(CompareState.Different, byName["diff.txt"].State);
        Assert.Equal(CompareState.LeftOnly, byName["left.txt"].State);
        Assert.Equal(CompareState.RightOnly, byName["right.txt"].State);
        Assert.Equal(CompareState.Different, byName["newer.txt"].State);
        Assert.Equal(NewerSide.Left, byName["newer.txt"].Newer);
    }

    [Fact]
    public async Task Content_mode_ignores_timestamps()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        l.File("a.bin", "same-content", new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        r.File("a.bin", "same-content");
        l.File("b.bin", "content-1");
        r.File("b.bin", "content-2");

        var rows = await new FolderComparer().CompareLevelAsync(l.Path, r.Path, NoFilter, new CompareOptions { Method = CompareMethod.Content });
        Assert.Equal(CompareState.Same, rows.Single(x => x.Name == "a.bin").State);
        Assert.Equal(CompareState.Different, rows.Single(x => x.Name == "b.bin").State);
    }

    [Fact]
    public async Task Directories_resolve_recursively_and_respect_filter()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        l.File("sub/deep/x.txt", "1");
        r.File("sub/deep/x.txt", "1");
        l.File("sub2/x.txt", "1");
        r.File("sub2/x.txt", "22");
        l.File("sub3/x.tmp", "only left, filtered");
        r.Dir("sub3");

        var filter = FilterSet.Create(new FilterSettings { Exclude = "*.tmp" }, true);
        var comparer = new FolderComparer();
        var rows = await comparer.CompareLevelAsync(l.Path, r.Path, filter, new CompareOptions());
        Assert.All(rows, x => Assert.Equal(CompareState.Pending, x.State));

        var resolved = 0;
        await comparer.ResolveDirectoriesAsync(rows, filter, new CompareOptions(), _ => Interlocked.Increment(ref resolved));
        Assert.Equal(3, resolved);
        Assert.Equal(CompareState.Same, rows.Single(x => x.Name == "sub").State);
        Assert.Equal(CompareState.Different, rows.Single(x => x.Name == "sub2").State);
        Assert.Equal(CompareState.Same, rows.Single(x => x.Name == "sub3").State);

        // 캐시: 다시 비교하면 바로 상태가 채워짐
        var again = await comparer.CompareLevelAsync(l.Path, r.Path, filter, new CompareOptions());
        Assert.DoesNotContain(again, x => x.State == CompareState.Pending);
    }

    [Fact]
    public async Task Missing_side_directory_is_empty()
    {
        using var l = new TempDir();
        l.File("a.txt", "1");
        var rows = await new FolderComparer().CompareLevelAsync(l.Path, Path.Combine(l.Path, "nope"), NoFilter, new CompareOptions());
        Assert.Equal(CompareState.LeftOnly, Assert.Single(rows).State);
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(2, false, true)]
    [InlineData(3, false, false)]
    [InlineData(3600, false, false)]
    [InlineData(3600, true, true)]
    [InlineData(3601, true, true)]
    public void Time_tolerance(int seconds, bool ignoreHour, bool equal)
    {
        var a = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(equal, FolderComparer.TimesEqual(a, a.AddSeconds(seconds), new CompareOptions { IgnoreHourOffset = ignoreHour }));
    }

    [Fact]
    public void Merge_sorts_directories_first_naturally()
    {
        static FileSystem.FileEntry E(string n, bool d = false) => new(n, "/" + n, d, 0, DateTime.UtcNow, FileAttributes.Normal);
        var rows = FolderComparer.Merge([E("file10"), E("file2"), E("Zdir", true)], [E("FILE2"), E("adir", true)], NoFilter);
        Assert.Equal(["adir", "Zdir", "file2", "file10"], rows.Select(r => r.Name));
        Assert.False(rows.Single(r => r.Name == "file2").IsOneSided);
    }
}

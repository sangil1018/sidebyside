using System.Text;
using SideBySide.Core.FileSystem;
using SideBySide.Core.RecycleBin;

namespace SideBySide.Core.Tests;

public class RecycleBinTests
{
    private static byte[] IndexV2(string path, DateTime deletedUtc, long size)
    {
        var chars = path + "\0";
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(2L));
        bytes.AddRange(BitConverter.GetBytes(size));
        bytes.AddRange(BitConverter.GetBytes(deletedUtc.ToFileTimeUtc()));
        bytes.AddRange(BitConverter.GetBytes(chars.Length));
        bytes.AddRange(Encoding.Unicode.GetBytes(chars));
        return [.. bytes];
    }

    private static byte[] IndexV1(string path, DateTime deletedUtc)
    {
        var bytes = new byte[24 + 520];
        BitConverter.GetBytes(1L).CopyTo(bytes, 0);
        BitConverter.GetBytes(5L).CopyTo(bytes, 8);
        BitConverter.GetBytes(deletedUtc.ToFileTimeUtc()).CopyTo(bytes, 16);
        Encoding.Unicode.GetBytes(path).CopyTo(bytes, 24);
        return bytes;
    }

    [Fact]
    public void Parses_both_index_versions()
    {
        var when = new DateTime(2025, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal((@"C:\a\b.txt", when, 42L), RecycleBinStore.ParseIndex(IndexV2(@"C:\a\b.txt", when, 42)));
        Assert.Equal(@"C:\old.txt", RecycleBinStore.ParseIndex(IndexV1(@"C:\old.txt", when))!.Value.OriginalPath);
        Assert.Null(RecycleBinStore.ParseIndex(new byte[10]));
        Assert.Null(RecycleBinStore.ParseIndex(new byte[40]));
    }

    [Fact]
    public void Restores_newest_and_skips_existing()
    {
        using var d = new TempDir();
        var bin = d.Dir("bin");
        var target = Path.Combine(d.Path, "work", "doc.txt");
        var taken = d.File("taken.txt", "keep me");

        File.WriteAllBytes(Path.Combine(bin, "$IAAA.txt"), IndexV2(target, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), 3));
        File.WriteAllText(Path.Combine(bin, "$RAAA.txt"), "old");
        File.WriteAllBytes(Path.Combine(bin, "$IBBB.txt"), IndexV2(target, new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc), 3));
        File.WriteAllText(Path.Combine(bin, "$RBBB.txt"), "new");
        File.WriteAllBytes(Path.Combine(bin, "$ICCC.txt"), IndexV2(taken, DateTime.UtcNow, 3));
        File.WriteAllText(Path.Combine(bin, "$RCCC.txt"), "recycled");

        var restored = RecycleBinStore.Restore([target, taken], _ => bin);

        Assert.Equal([target], restored);
        Assert.Equal("new", File.ReadAllText(target));
        Assert.False(File.Exists(Path.Combine(bin, "$IBBB.txt")));
        Assert.True(File.Exists(Path.Combine(bin, "$RAAA.txt")));
        Assert.Equal("keep me", File.ReadAllText(taken));
    }

    [Fact]
    public void Restores_directories()
    {
        using var d = new TempDir();
        var bin = d.Dir("bin");
        var target = Path.Combine(d.Path, "folder");
        File.WriteAllBytes(Path.Combine(bin, "$IXYZ"), IndexV2(target, DateTime.UtcNow, 0));
        File.WriteAllText(Path.Combine(d.Dir(Path.Combine("bin", "$RXYZ")), "inner.txt"), "x");

        Assert.Single(RecycleBinStore.Restore([target], _ => bin));
        Assert.True(File.Exists(Path.Combine(target, "inner.txt")));
    }
}

public class DirectorySearchTests
{
    [Fact]
    public void Recursive_search_prunes_and_matches()
    {
        using var d = new TempDir();
        d.File("a/report.txt", "1");
        d.File("a/deep/report-2.txt", "1");
        d.File("bin/report.txt", "excluded folder");
        d.File("other.md", "1");

        var found = DirectoryLoader.Search(
                d.Path,
                (name, isDir, _) => !isDir && name.Contains("report", StringComparison.OrdinalIgnoreCase),
                (name, _) => name != "bin")
            .Select(e => Path.GetRelativePath(d.Path, e.FullPath).Replace('\\', '/'))
            .Order()
            .ToList();

        Assert.Equal(["a/deep/report-2.txt", "a/report.txt"], found);
    }
}

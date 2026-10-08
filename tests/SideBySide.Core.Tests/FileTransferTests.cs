using SideBySide.Core.FileSystem;

namespace SideBySide.Core.Tests;

public class FileTransferTests
{
    private static readonly DateTime OldTime = new(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);

    private static Func<TransferConflict, Task<ConflictChoice>> Always(ConflictChoice choice, List<TransferConflict>? seen = null)
        => c =>
        {
            seen?.Add(c);
            return Task.FromResult(choice);
        };

    private sealed class SyncProgress(List<TransferProgress> list) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => list.Add(value);
    }

    [Fact]
    public async Task Copies_tree_and_preserves_timestamps()
    {
        using var d = new TempDir();
        d.File("src/a.txt", "aaa", OldTime);
        d.File("src/sub/b.txt", "bb", OldTime);
        var dest = d.Dir("dest");
        var progress = new List<TransferProgress>();

        var result = await FileTransfer.RunAsync(TransferKind.Copy, [Path.Combine(d.Path, "src")], dest, Always(ConflictChoice.Skip), new SyncProgress(progress));

        Assert.False(result.Cancelled);
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.FilesProcessed);
        Assert.Equal("bb", File.ReadAllText(Path.Combine(dest, "src", "sub", "b.txt")));
        Assert.Equal(OldTime, File.GetLastWriteTimeUtc(Path.Combine(dest, "src", "a.txt")));
        Assert.True(File.Exists(Path.Combine(d.Path, "src", "a.txt")));
        var item = Assert.Single(result.Items);
        Assert.True(item.CreatedNew);

        Assert.Equal(5, progress[^1].BytesTotal);
        Assert.Equal(5, progress[^1].BytesDone);
        Assert.Equal(1.0, progress[^1].Fraction);
        for (int i = 1; i < progress.Count; i++) Assert.True(progress[i].BytesDone >= progress[i - 1].BytesDone);
    }

    [Fact]
    public async Task Large_file_streams_with_progress()
    {
        using var d = new TempDir();
        var big = Path.Combine(d.Path, "big.bin");
        var data = new byte[9 * 1024 * 1024];
        new Random(1).NextBytes(data);
        File.WriteAllBytes(big, data);
        File.SetLastWriteTimeUtc(big, OldTime);
        var dest = d.Dir("dest");
        var progress = new List<TransferProgress>();

        await FileTransfer.RunAsync(TransferKind.Copy, [big], dest, Always(ConflictChoice.Skip), new SyncProgress(progress));

        Assert.Equal(data, File.ReadAllBytes(Path.Combine(dest, "big.bin")));
        Assert.Equal(OldTime, File.GetLastWriteTimeUtc(Path.Combine(dest, "big.bin")));
        Assert.Equal(data.Length, progress[^1].BytesDone);
    }

    [Theory]
    [InlineData(ConflictChoice.Overwrite, "new", "a.txt")]
    [InlineData(ConflictChoice.Skip, "old", "a.txt")]
    [InlineData(ConflictChoice.KeepBoth, "old", "a (2).txt")]
    public async Task Conflicts(ConflictChoice choice, string expectedOriginal, string expectedNewFile)
    {
        using var d = new TempDir();
        var src = d.File("src/a.txt", "new");
        d.File("dest/a.txt", "old");
        var dest = Path.Combine(d.Path, "dest");
        var seen = new List<TransferConflict>();

        var result = await FileTransfer.RunAsync(TransferKind.Copy, [src], dest, Always(choice, seen));

        var conflict = Assert.Single(seen);
        Assert.True(conflict.CanOverwrite);
        Assert.Equal(3, conflict.SourceSize);
        Assert.Equal(expectedOriginal, File.ReadAllText(Path.Combine(dest, "a.txt")));
        if (choice != ConflictChoice.Skip) Assert.Equal("new", File.ReadAllText(Path.Combine(dest, expectedNewFile)));
        Assert.Equal(choice == ConflictChoice.Skip ? 1 : 0, result.Skipped);
    }

    [Fact]
    public async Task Cancel_choice_stops_transfer()
    {
        using var d = new TempDir();
        var a = d.File("src/a.txt", "1");
        var b = d.File("src/b.txt", "2");
        d.File("dest/a.txt", "x");
        var dest = Path.Combine(d.Path, "dest");

        var result = await FileTransfer.RunAsync(TransferKind.Copy, [a, b], dest, Always(ConflictChoice.Cancel));

        Assert.True(result.Cancelled);
        Assert.False(File.Exists(Path.Combine(dest, "b.txt")));
    }

    [Fact]
    public async Task Copy_into_same_folder_creates_copy_names()
    {
        using var d = new TempDir();
        var file = d.File("a.txt", "1");
        var dir = d.Dir("folder");

        var first = await FileTransfer.RunAsync(TransferKind.Copy, [file, dir], d.Path, Always(ConflictChoice.Skip));
        var second = await FileTransfer.RunAsync(TransferKind.Copy, [file], d.Path, Always(ConflictChoice.Skip));

        Assert.True(File.Exists(Path.Combine(d.Path, "a - 복사본.txt")));
        Assert.True(Directory.Exists(Path.Combine(d.Path, "folder - 복사본")));
        Assert.Equal(Path.Combine(d.Path, "a - 복사본 (2).txt"), Assert.Single(second.Items).Destination);
        Assert.All(first.Items, i => Assert.True(i.CreatedNew));
    }

    [Fact]
    public async Task Move_merges_into_existing_folder_and_removes_source()
    {
        using var d = new TempDir();
        d.File("src/data/a.txt", "a");
        d.File("src/data/sub/b.txt", "b");
        d.File("dest/data/existing.txt", "e");
        var dest = Path.Combine(d.Path, "dest");

        var result = await FileTransfer.RunAsync(TransferKind.Move, [Path.Combine(d.Path, "src", "data")], dest, Always(ConflictChoice.Skip));

        Assert.Empty(result.Errors);
        Assert.False(Directory.Exists(Path.Combine(d.Path, "src", "data")));
        Assert.True(File.Exists(Path.Combine(dest, "data", "a.txt")));
        Assert.True(File.Exists(Path.Combine(dest, "data", "sub", "b.txt")));
        Assert.True(File.Exists(Path.Combine(dest, "data", "existing.txt")));
        Assert.False(Assert.Single(result.Items).CreatedNew);
    }

    [Fact]
    public async Task Move_whole_folder_on_same_volume()
    {
        using var d = new TempDir();
        d.File("src/data/a.txt", "a");
        var dest = d.Dir("dest");

        var result = await FileTransfer.RunAsync(TransferKind.Move, [Path.Combine(d.Path, "src", "data")], dest, Always(ConflictChoice.Skip));

        Assert.Equal(1, result.FilesProcessed);
        Assert.True(File.Exists(Path.Combine(dest, "data", "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(d.Path, "src", "data")));
    }

    [Fact]
    public async Task Refuses_copy_into_itself()
    {
        using var d = new TempDir();
        var src = d.Dir("src");
        var inner = d.Dir("src/inner");

        var result = await FileTransfer.RunAsync(TransferKind.Copy, [src], inner, Always(ConflictChoice.Skip));

        Assert.Single(result.Errors);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Cancellation_token_removes_partial_file()
    {
        using var d = new TempDir();
        var big = Path.Combine(d.Path, "big.bin");
        File.WriteAllBytes(big, new byte[16 * 1024 * 1024]);
        var dest = d.Dir("dest");
        using var cts = new CancellationTokenSource();
        var cancelling = new CancelAfterFirst(cts);

        // 매 버퍼마다 보고하게 해서 "첫 1MB를 쓴 뒤 취소"가 디스크 속도와 무관하게 일어나도록
        var interval = FileTransfer.ReportIntervalMs;
        FileTransfer.ReportIntervalMs = 0;
        TransferResult result;
        try
        {
            result = await FileTransfer.RunAsync(TransferKind.Copy, [big], dest, Always(ConflictChoice.Skip), cancelling, cts.Token);
        }
        finally
        {
            FileTransfer.ReportIntervalMs = interval;
        }

        Assert.True(result.Cancelled);
        Assert.False(File.Exists(Path.Combine(dest, "big.bin")));
    }

    private sealed class CancelAfterFirst(CancellationTokenSource cts) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value)
        {
            if (value.BytesDone > 0) cts.Cancel();
        }
    }
}

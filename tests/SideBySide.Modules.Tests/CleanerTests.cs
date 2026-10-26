using System.Diagnostics;
using System.Text.Json;
using SideBySide.Modules.TempCleaner;

namespace SideBySide.Modules.Tests;

public class CleanerTests
{
    private static CleanTarget Target(string root) => new() { Id = "test", Name = "test", Group = "test", Paths = [root] };

    /// <summary>관리자 권한 없이 만들 수 있는 정션.</summary>
    private static bool MakeJunction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.WaitForExit();
        return p.ExitCode == 0 && Directory.Exists(link);
    }

    [Fact]
    public void Clean_does_not_follow_junctions()
    {
        using var d = new TempDir();
        var root = d.Dir("cache");
        d.File(@"cache\a.tmp", "1");
        d.File(@"cache\sub\b.tmp", "22");
        var outside = d.File(@"precious\keep.txt", "do not delete");
        Assert.True(MakeJunction(Path.Combine(root, "link"), Path.Combine(d.Path, "precious")));

        var scanned = Cleaner.Scan(Target(root), 0);
        Assert.Equal(2, scanned.Count);

        var result = Cleaner.Clean(Target(root), 0);

        Assert.Equal(2, result.DeletedFiles);
        Assert.Equal(3, result.FreedBytes);
        Assert.True(File.Exists(outside));
        Assert.True(Directory.Exists(Path.Combine(root, "link"))); // 링크 자체도 건드리지 않음
        Assert.False(Directory.Exists(Path.Combine(root, "sub"))); // 비게 된 하위 폴더 정리
        Directory.Delete(Path.Combine(root, "link")); // 정션만 제거
    }

    [Fact]
    public void Age_filter_keeps_recent_files()
    {
        using var d = new TempDir();
        var root = d.Dir("cache");
        var old = d.File(@"cache\old.tmp", "x", DateTime.UtcNow.AddDays(-30));
        var fresh = d.File(@"cache\new.tmp", "x", DateTime.UtcNow);

        var result = Cleaner.Clean(Target(root), 7);

        Assert.Equal(1, result.DeletedFiles);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Locked_files_are_skipped()
    {
        using var d = new TempDir();
        var root = d.Dir("cache");
        var locked = d.File(@"cache\locked.tmp", "x");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = Cleaner.Clean(Target(root), 0);

        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(1, result.SkippedFiles);
    }

    [Fact]
    public void DeleteFiles_rejects_paths_outside_category_and_through_links()
    {
        using var d = new TempDir();
        var root = d.Dir("cache");
        var inside = d.File(@"cache\in.tmp", "x");
        var outside = d.File(@"other\out.txt", "x");
        var behindLink = d.File(@"other\linked.txt", "x");
        Assert.True(MakeJunction(Path.Combine(root, "link"), Path.Combine(d.Path, "other")));

        var result = Cleaner.DeleteFiles(Target(root), [inside, outside, Path.Combine(root, @"..\other\out.txt"), Path.Combine(root, "link", "linked.txt")]);

        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(3, result.SkippedFiles);
        Assert.False(File.Exists(inside));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(behindLink));
        Directory.Delete(Path.Combine(root, "link"));
    }

    [Fact]
    public void ListFiles_sorts_by_size_and_truncates()
    {
        using var d = new TempDir();
        var root = d.Dir("cache");
        d.File(@"cache\s.tmp", "1");
        d.File(@"cache\l.tmp", "333");
        d.File(@"cache\m.tmp", "22");

        var (files, truncated) = Cleaner.ListFiles(Target(root), 0, 2);

        Assert.True(truncated);
        Assert.Equal(["l.tmp", "m.tmp"], files.Select(f => Path.GetFileName(f.Path)));
    }

    [Fact]
    public void Elevated_clean_only_accepts_system_category_ids()
    {
        // 사용자 영역 카테고리 ID나 모르는 ID는 승격 프로세스에서 거부 (실제 삭제 없음)
        var payload = JsonSerializer.Serialize(new CleanRequest { Ids = ["user-temp", "chrome", "../../etc", "recyclebin"] });
        var response = JsonSerializer.Deserialize<CleanResponse>(CleanerElevated.Clean(payload))!;

        Assert.Empty(response.Results);
        Assert.Equal(4, response.Rejected.Count);
    }

    [Fact]
    public void Secure_delete_refuses_files_reached_through_a_swapped_junction()
    {
        // 검사 뒤 공격자가 하위 폴더를 정션으로 바꿔치기한 상황: 경로는 대상 안처럼 보이지만 실제 위치는 밖
        using var d = new TempDir();
        var root = d.Dir("cache");
        var victim = d.File(@"system\important.dll", "keep");
        Assert.True(MakeJunction(Path.Combine(root, "sub"), Path.Combine(d.Path, "system")));
        var finalRoot = SecureDelete.FinalDirectoryPath(root)!;

        Assert.False(SecureDelete.TryDelete(Path.Combine(root, "sub", "important.dll"), finalRoot, out _));
        Assert.True(File.Exists(victim));
        Directory.Delete(Path.Combine(root, "sub"));
    }

    [Fact]
    public void Secure_delete_removes_read_only_files_inside_root()
    {
        using var d = new TempDir();
        var root = d.Dir("cache");
        var file = d.File(Path.Combine("cache", "ro.tmp"), "12345");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        Assert.True(SecureDelete.TryDelete(file, SecureDelete.FinalDirectoryPath(root)!, out var length));
        Assert.Equal(5, length);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Secure_delete_skips_locked_and_missing_files()
    {
        using var d = new TempDir();
        var root = d.Dir("cache");
        var finalRoot = SecureDelete.FinalDirectoryPath(root)!;
        var locked = d.File(@"cache\locked.tmp", "x");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(SecureDelete.TryDelete(locked, finalRoot, out _));
        }

        Assert.False(SecureDelete.TryDelete(Path.Combine(root, "none.tmp"), finalRoot, out _));
    }

    [Fact]
    public void Formats_bytes() => Assert.Equal("1.5 KB", Cleaner.FormatBytes(1536));
}

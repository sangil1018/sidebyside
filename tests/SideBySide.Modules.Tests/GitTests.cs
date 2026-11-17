using SideBySide.Core.FileSystem;
using SideBySide.Modules.Git;

namespace SideBySide.Modules.Tests;

public class GitTests
{
    [Fact]
    public void Status_reads_branch_upstream_and_counts()
    {
        var output = string.Join('\0',
            "# branch.oid 1234567890abcdef",
            "# branch.head feature/login",
            "# branch.upstream origin/feature/login",
            "# branch.ab +2 -3",
            "");

        var status = GitParsers.ParseStatus(output);

        Assert.Equal("feature/login", status.Branch);
        Assert.False(status.Detached);
        Assert.Equal("origin/feature/login", status.Upstream);
        Assert.Equal(2, status.Ahead);
        Assert.Equal(3, status.Behind);
        Assert.Empty(status.Changes);
    }

    [Fact]
    public void Status_reads_detached_head_without_upstream()
    {
        var status = GitParsers.ParseStatus("# branch.oid abc\0# branch.head (detached)\0");

        Assert.Null(status.Branch);
        Assert.True(status.Detached);
        Assert.Null(status.Upstream);
    }

    [Fact]
    public void Status_reads_every_kind_of_change()
    {
        var output = string.Join('\0',
            "# branch.head main",
            "1 .M N... 100644 100644 100644 aaa bbb src/app file.cs",
            "1 A. N... 000000 100644 100644 000 bbb docs/new.md",
            "1 .D N... 100644 100644 000000 aaa aaa old.txt",
            "1 D. N... 100644 000000 000000 aaa 000 staged-delete.txt",
            "2 R. N... 100644 100644 100644 aaa bbb R100 renamed to.txt",
            "renamed from.txt",
            "u UU N... 100644 100644 100644 100644 aaa bbb ccc conflict.txt",
            "? untracked dir/file.bin",
            "! ignored.log",
            "");

        var changes = GitParsers.ParseStatus(output).Changes;

        Assert.Equal(
        [
            new GitChange("src/app file.cs", null, GitChangeKind.Modified),
            new GitChange("docs/new.md", null, GitChangeKind.Added),
            new GitChange("old.txt", null, GitChangeKind.Deleted),
            new GitChange("staged-delete.txt", null, GitChangeKind.Deleted),
            new GitChange("renamed to.txt", "renamed from.txt", GitChangeKind.Renamed),
            new GitChange("conflict.txt", null, GitChangeKind.Conflicted),
            new GitChange("untracked dir/file.bin", null, GitChangeKind.Untracked),
        ], changes);
    }

    [Fact]
    public void Log_splits_records_and_keeps_multiline_body()
    {
        const char F = GitParsers.FieldSeparator;
        const char R = GitParsers.RecordSeparator;
        var output =
            $"aaaa1111{F}aaaa{F}Kim{F}2026-11-01T10:20:30+09:00{F}첫 줄 제목{F}본문 1\n본문 2\n{R}\n" +
            $"bbbb2222{F}bbbb{F}Lee{F}2026-10-31T08:00:00+00:00{F}Second{F}{R}\n";

        var commits = GitParsers.ParseLog(output);

        Assert.Equal(2, commits.Count);
        Assert.Equal("aaaa1111", commits[0].Hash);
        Assert.Equal("aaaa", commits[0].ShortHash);
        Assert.Equal("Kim", commits[0].Author);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 10, 20, 30, TimeSpan.FromHours(9)), commits[0].Date);
        Assert.Equal("첫 줄 제목", commits[0].Subject);
        Assert.Equal("본문 1\n본문 2", commits[0].Body);
        Assert.Equal("Second", commits[1].Subject);
        Assert.Equal(string.Empty, commits[1].Body);
    }

    [Fact]
    public void Branches_hide_remote_duplicates_and_remote_head()
    {
        var output =
            "*\trefs/heads/main\n" +
            " \trefs/heads/feature/a\n" +
            " \trefs/remotes/origin/HEAD\n" +
            " \trefs/remotes/origin/main\n" +
            " \trefs/remotes/origin/feature/b\n";

        var branches = GitParsers.ParseBranches(output);

        Assert.Equal(
        [
            new GitBranch("main", false, true),
            new GitBranch("feature/a", false, false),
            new GitBranch("origin/feature/b", true, false),
        ], branches);
    }

    [Fact]
    public void NameStatus_reads_renames_with_two_paths()
    {
        var output = "M\0src/a.cs\0A\0b.txt\0D\0c.txt\0R087\0old name.txt\0new name.txt\0";

        var files = GitParsers.ParseNameStatus(output);

        Assert.Equal(
        [
            new GitChange("src/a.cs", null, GitChangeKind.Modified),
            new GitChange("b.txt", null, GitChangeKind.Added),
            new GitChange("c.txt", null, GitChangeKind.Deleted),
            new GitChange("new name.txt", "old name.txt", GitChangeKind.Renamed),
        ], files);
    }

    [Fact]
    public void Diff_numbers_lines_and_drops_file_headers()
    {
        var output =
            "diff --git a/a.txt b/a.txt\n" +
            "index 111..222 100644\n" +
            "--- a/a.txt\n" +
            "+++ b/a.txt\n" +
            "@@ -10,4 +10,5 @@ void Main()\n" +
            " keep\n" +
            "-gone\n" +
            "+added 1\n" +
            "+added 2\n" +
            " tail\n" +
            "\\ No newline at end of file\n";

        var lines = GitParsers.ParseDiff(output);

        Assert.Equal(
        [
            new DiffLine(DiffLineKind.Hunk, "@@ -10,4 +10,5 @@ void Main()"),
            new DiffLine(DiffLineKind.Context, "keep", 10, 10),
            new DiffLine(DiffLineKind.Removed, "gone", 11, null),
            new DiffLine(DiffLineKind.Added, "added 1", null, 11),
            new DiffLine(DiffLineKind.Added, "added 2", null, 12),
            new DiffLine(DiffLineKind.Context, "tail", 12, 13),
            new DiffLine(DiffLineKind.Note, "No newline at end of file"),
        ], lines);
    }

    [Fact]
    public void Diff_keeps_notes_for_binary_and_new_files()
    {
        var output =
            "diff --git a/img.png b/img.png\n" +
            "new file mode 100644\n" +
            "index 000..abc\n" +
            "Binary files /dev/null and b/img.png differ\n";

        var lines = GitParsers.ParseDiff(output);

        Assert.Equal(["new file mode 100644", "Binary files /dev/null and b/img.png differ"], lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.Equal(DiffLineKind.Note, l.Kind));
    }

    [Theory]
    [InlineData(@"C:\repo\src\a.cs", true)]
    [InlineData(@"C:\repo\.gitignore", true)]
    [InlineData(@"C:\repo\.git", false)]
    [InlineData(@"C:\repo\.git\HEAD", true)]
    [InlineData(@"C:\repo\.git\index", true)]
    [InlineData(@"C:\repo\.git\index.lock", false)]
    [InlineData(@"C:\repo\.git\refs\heads\main", true)]
    [InlineData(@"C:\repo\.git\refs\heads\main.lock", false)]
    [InlineData(@"C:\repo\.git\objects\ab\cdef", false)]
    public void Watcher_ignores_git_internals_except_state_files(string path, bool relevant)
        => Assert.Equal(relevant, GitViewModel.IsRelevant(@"C:\repo", path));

    [Fact]
    public void RelativeTime_steps_from_minutes_to_date()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(Core.Localization.L.T("방금"), GitViewModel.RelativeTime(now.AddSeconds(-20), now));
        Assert.Equal(Core.Localization.L.F("{0}분 전", 5), GitViewModel.RelativeTime(now.AddMinutes(-5), now));
        Assert.Equal(Core.Localization.L.F("{0}시간 전", 3), GitViewModel.RelativeTime(now.AddHours(-3), now));
        Assert.Equal(Core.Localization.L.F("{0}일 전", 4), GitViewModel.RelativeTime(now.AddDays(-4), now));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", GitViewModel.RelativeTime(now.AddDays(-90), now));
    }

    [Fact]
    public void FindRoot_walks_up_to_the_folder_holding_dot_git()
    {
        using var temp = new TempDir();
        var repo = Path.Combine(temp.Path, "repo");
        var nested = Path.Combine(repo, "src", "deep");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(nested);
        var file = Path.Combine(nested, "a.txt");
        File.WriteAllText(file, "x");

        // 워크트리·서브모듈은 .git이 파일이다
        var worktree = Path.Combine(temp.Path, "worktree");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: elsewhere");

        Assert.True(GitRepository.SameRoot(repo, GitRepository.FindRoot(nested)));
        Assert.True(GitRepository.SameRoot(repo, GitRepository.FindRoot(file)));
        Assert.True(GitRepository.SameRoot(worktree, GitRepository.FindRoot(worktree)));
        Assert.Null(GitRepository.FindRoot(null));
        Assert.True(GitRepository.SameRoot(@"C:\Repo\", @"c:\repo"));
        Assert.False(GitRepository.SameRoot(null, null));
    }
}

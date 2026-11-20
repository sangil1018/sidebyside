using SideBySide.Core.FileSystem;
using SideBySide.Modules.Svn;

namespace SideBySide.Modules.Tests;

public class SvnTests
{
    [Fact]
    public void Status_maps_working_copy_items_and_skips_clean_ones()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <status>
            <target path=".">
            <entry path="src\app file.cs"><wc-status item="modified" props="none" revision="5" /></entry>
            <entry path="docs\new.md"><wc-status item="added" props="none" revision="-1" /></entry>
            <entry path="old.txt"><wc-status item="deleted" props="none" revision="5" /></entry>
            <entry path="gone.txt"><wc-status item="missing" props="none" /></entry>
            <entry path="scratch"><wc-status item="unversioned" props="none" /></entry>
            <entry path="merge.txt"><wc-status item="conflicted" props="none" revision="5" /></entry>
            <entry path="tree.txt"><wc-status item="modified" props="none" tree-conflicted="true" revision="5" /></entry>
            <entry path="props-only"><wc-status item="normal" props="modified" revision="5" /></entry>
            <entry path="replaced.bin"><wc-status item="replaced" props="none" revision="5" /></entry>
            <entry path="ignored.log"><wc-status item="ignored" props="none" /></entry>
            <entry path="lib"><wc-status item="external" props="none" /></entry>
            <entry path="."><wc-status item="normal" props="none" revision="5" /></entry>
            </target>
            </status>
            """;

        var status = SvnParsers.ParseStatus(xml);

        Assert.Null(status.Incoming);
        Assert.Equal(
        [
            new SvnChange("src/app file.cs", SvnChangeKind.Modified),
            new SvnChange("docs/new.md", SvnChangeKind.Added),
            new SvnChange("old.txt", SvnChangeKind.Deleted),
            new SvnChange("gone.txt", SvnChangeKind.Missing),
            new SvnChange("scratch", SvnChangeKind.Unversioned),
            new SvnChange("merge.txt", SvnChangeKind.Conflicted),
            new SvnChange("tree.txt", SvnChangeKind.Conflicted),
            new SvnChange("props-only", SvnChangeKind.Modified),
            new SvnChange("replaced.bin", SvnChangeKind.Modified),
        ], status.Changes);
    }

    [Fact]
    public void Status_with_server_check_counts_incoming_changes()
    {
        const string xml = """
            <status><target path=".">
            <entry path="a.txt"><wc-status item="normal" props="none" revision="5" /><repos-status item="modified" props="none" /></entry>
            <entry path="b.txt"><wc-status item="modified" props="none" revision="5" /><repos-status item="none" props="modified" /></entry>
            <entry path="c.txt"><wc-status item="modified" props="none" revision="5" /></entry>
            <entry path="d.txt"><wc-status item="none" props="none" /><repos-status item="added" props="none" /></entry>
            <against revision="9" />
            </target></status>
            """;

        var status = SvnParsers.ParseStatus(xml);

        Assert.Equal(3, status.Incoming);
        Assert.Equal(["b.txt", "c.txt"], status.Changes.Select(c => c.Path));
    }

    [Fact]
    public void Info_reads_url_relative_url_and_revision()
    {
        const string xml = """
            <info><entry kind="dir" path="." revision="1234">
            <url>https://svn.example.com/repo/project/branches/my%20branch</url>
            <relative-url>^/project/branches/my%20branch</relative-url>
            <repository><root>https://svn.example.com/repo</root><uuid>x</uuid></repository>
            </entry></info>
            """;

        var info = SvnParsers.ParseInfo(xml);

        Assert.Equal("^/project/branches/my branch", info.RelativeUrl);
        Assert.Equal("https://svn.example.com/repo", info.RepositoryRoot);
        Assert.Equal(1234, info.Revision);
    }

    [Fact]
    public void Info_builds_relative_url_for_old_clients()
    {
        const string xml = """
            <info><entry kind="dir" path="." revision="7">
            <url>https://svn.example.com/repo/trunk</url>
            <repository><root>https://svn.example.com/repo</root></repository>
            </entry></info>
            """;

        Assert.Equal("^/trunk", SvnParsers.ParseInfo(xml).RelativeUrl);
    }

    [Theory]
    [InlineData("^/trunk", "", "/trunk", "trunk", "")]
    [InlineData("^/project/trunk/src/app", "/project", "/project/trunk", "trunk", "/src/app")]
    [InlineData("^/branches/feature-x", "", "/branches/feature-x", "feature-x", "")]
    [InlineData("^/a/b/tags/v1.0/docs", "/a/b", "/a/b/tags/v1.0", "tags/v1.0", "/docs")]
    public void Layout_finds_trunk_branches_and_tags(string relativeUrl, string basePath, string branch, string name, string subPath)
        => Assert.Equal(new SvnLayout(basePath, branch, name, subPath), SvnParsers.ParseLayout(relativeUrl));

    [Theory]
    [InlineData("^/")]
    [InlineData("^/project/src")]
    [InlineData("^/branches")]
    public void Layout_is_null_for_non_standard_paths(string relativeUrl)
        => Assert.Null(SvnParsers.ParseLayout(relativeUrl));

    [Fact]
    public void Branches_list_trunk_then_branches_and_mark_current()
    {
        var names = SvnParsers.ParseList("zeta/\r\nalpha/\r\nREADME.txt\r\n");
        Assert.Equal(["zeta", "alpha"], names);

        var onBranch = SvnParsers.BuildBranches(new SvnLayout("/p", "/p/branches/zeta", "zeta", ""), names);
        Assert.Equal(
        [
            new SvnBranch("trunk", "/p/trunk", false),
            new SvnBranch("alpha", "/p/branches/alpha", false),
            new SvnBranch("zeta", "/p/branches/zeta", true),
        ], onBranch);

        // 태그를 보고 있으면 목록 끝에 지금 위치를 붙인다
        var onTag = SvnParsers.BuildBranches(new SvnLayout("", "/tags/v1", "tags/v1", ""), []);
        Assert.Equal([new SvnBranch("trunk", "/trunk", false), new SvnBranch("tags/v1", "/tags/v1", true)], onTag);
    }

    [Fact]
    public void Log_reads_revisions_paths_and_splits_message()
    {
        const string xml = """
            <log>
            <logentry revision="42">
            <author>kim</author>
            <date>2026-11-01T01:20:30.123456Z</date>
            <paths>
            <path action="M" kind="file">/trunk/src/a.cs</path>
            <path action="A" kind="dir" copyfrom-path="/trunk" copyfrom-rev="41">/branches/x</path>
            </paths>
            <msg>첫 줄 제목

            본문 1
            본문 2</msg>
            </logentry>
            <logentry revision="41"><author>lee</author><date>2026-10-31T08:00:00.000000Z</date><msg></msg></logentry>
            </log>
            """;

        var log = SvnParsers.ParseLog(xml);

        Assert.Equal(2, log.Count);
        Assert.Equal(42, log[0].Revision);
        Assert.Equal("kim", log[0].Author);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 1, 20, 30, 123, TimeSpan.Zero).AddTicks(4560), log[0].Date);
        Assert.Equal("첫 줄 제목", log[0].Subject);
        Assert.Equal("본문 1\n본문 2", log[0].Body.ReplaceLineEndings("\n"));
        Assert.Equal([new SvnLogPath("/trunk/src/a.cs", 'M', false), new SvnLogPath("/branches/x", 'A', true)], log[0].Paths);
        Assert.Equal(string.Empty, log[1].Subject);
        Assert.Empty(log[1].Paths);
    }

    [Theory]
    [InlineData("/trunk/src/a.cs", "/trunk", "src/a.cs")]
    [InlineData("/trunk/src/a.cs", "/trunk/src", "a.cs")]
    [InlineData("/branches/x/a.cs", "/trunk", "/branches/x/a.cs")]
    [InlineData("/trunkfile.txt", "/trunk", "/trunkfile.txt")]
    public void DisplayPath_is_relative_to_the_checkout_when_inside_it(string repositoryPath, string checkout, string expected)
        => Assert.Equal(expected, SvnParsers.DisplayPath(repositoryPath, checkout));

    [Fact]
    public void Diff_drops_svn_headers_and_numbers_lines()
    {
        var output =
            "Index: src/a.txt\r\n" +
            "===================================================================\r\n" +
            "--- src/a.txt\t(revision 5)\r\n" +
            "+++ src/a.txt\t(working copy)\r\n" +
            "@@ -10,3 +10,4 @@\r\n" +
            " keep\r\n" +
            "-gone\r\n" +
            "+added 1\r\n" +
            "+added 2\r\n" +
            "\\ No newline at end of file\r\n" +
            "\r\n" +
            "Property changes on: src/a.txt\r\n" +
            "___________________________________________________________________\r\n" +
            "Added: svn:eol-style\r\n";

        var lines = SvnParsers.ParseDiff(output);

        Assert.Equal(
        [
            new DiffLine(DiffLineKind.Hunk, "@@ -10,3 +10,4 @@"),
            new DiffLine(DiffLineKind.Context, "keep", 10, 10),
            new DiffLine(DiffLineKind.Removed, "gone", 11, null),
            new DiffLine(DiffLineKind.Added, "added 1", null, 11),
            new DiffLine(DiffLineKind.Added, "added 2", null, 12),
            new DiffLine(DiffLineKind.Note, "No newline at end of file"),
            new DiffLine(DiffLineKind.Note, "Property changes on: src/a.txt"),
            new DiffLine(DiffLineKind.Note, "Added: svn:eol-style"),
        ], lines);
    }

    [Fact]
    public void Diff_keeps_binary_notice()
    {
        var output =
            "Index: img.png\n" +
            "===================================================================\n" +
            "Cannot display: file marked as a binary type.\n" +
            "svn:mime-type = application/octet-stream\n";

        Assert.Equal(["Cannot display: file marked as a binary type.", "svn:mime-type = application/octet-stream"],
            SvnParsers.ParseDiff(output).Select(l => l.Text));
    }

    [Fact]
    public void WholeFile_marks_every_line_added_or_removed()
    {
        Assert.Equal(
            [new DiffLine(DiffLineKind.Added, "a", null, 1), new DiffLine(DiffLineKind.Added, "b", null, 2)],
            SvnParsers.WholeFile("a\r\nb\r\n", added: true));
        Assert.Equal(
            [new DiffLine(DiffLineKind.Removed, "x", 1, null)],
            SvnParsers.WholeFile("x", added: false));
    }

    [Theory]
    [InlineData(@"C:\wc\src\a.cs", true)]
    [InlineData(@"C:\wc\.svnignore", true)]
    [InlineData(@"C:\wc\.svn", false)]
    [InlineData(@"C:\wc\.svn\wc.db", false)]
    [InlineData(@"C:\wc\.svn\tmp\x", false)]
    public void Watcher_ignores_svn_admin_folder(string path, bool relevant)
        => Assert.Equal(relevant, SvnViewModel.IsRelevant(@"C:\wc", path));

    [Fact]
    public void FindRoot_walks_up_to_the_folder_holding_dot_svn()
    {
        using var temp = new TempDir();
        var wc = Path.Combine(temp.Path, "wc");
        var nested = Path.Combine(wc, "src", "deep");
        Directory.CreateDirectory(Path.Combine(wc, ".svn"));
        Directory.CreateDirectory(nested);
        var file = Path.Combine(nested, "a.txt");
        File.WriteAllText(file, "x");

        Assert.True(GitRepository.SameRoot(wc, SvnWorkingCopy.FindRoot(nested)));
        Assert.True(GitRepository.SameRoot(wc, SvnWorkingCopy.FindRoot(file)));
        Assert.Null(SvnWorkingCopy.FindRoot(temp.Path));
        Assert.Null(SvnWorkingCopy.FindRoot(null));
    }
}

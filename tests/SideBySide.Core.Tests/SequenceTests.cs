using SideBySide.Core.Sequences;

namespace SideBySide.Core.Tests;

public class SequenceTests
{
    [Theory]
    [InlineData("shot_010_0001.exr", "shot_010", "_", 1, 4, "exr")]
    [InlineData("frame-0012.png", "frame", "-", 12, 4, "png")]
    [InlineData("frame0012.png", "frame", "", 12, 4, "png")]
    [InlineData("shot_-005.png", "shot", "_", -5, 3, "png")]
    [InlineData("-005.png", "", "", -5, 3, "png")]
    [InlineData("0001.jpg", "", "", 1, 4, "jpg")]
    [InlineData("my shot.1001.tif", "my shot", ".", 1001, 4, "tif")]
    public void Parses_frame_names(string file, string prefix, string sep, int frame, int padding, string ext)
    {
        Assert.True(SequenceName.TryParse(file, out var name));
        Assert.Equal(prefix, name.Prefix);
        Assert.Equal(sep, name.Separator);
        Assert.Equal(frame, name.Frame);
        Assert.Equal(padding, name.Padding);
        Assert.Equal(ext, name.Extension);
    }

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("noext0001")]
    [InlineData(".0001")]
    [InlineData("shot_0001.")]
    [InlineData("big_12345678901.png")]
    public void Rejects_non_frames(string file) => Assert.False(SequenceName.TryParse(file, out _));

    [Theory]
    [InlineData("shot", "_", 7, 4, "exr", "shot_0007.exr")]
    [InlineData("shot", "_", -7, 3, "exr", "shot_-007.exr")]
    [InlineData("", "", 12, 2, "png", "12.png")]
    public void Formats(string prefix, string sep, int frame, int padding, string ext, string expected)
        => Assert.Equal(expected, SequenceName.Format(prefix, sep, frame, padding, ext));

    [Fact]
    public void Groups_by_folder_and_pattern()
    {
        using var d = new TempDir();
        foreach (var i in new[] { 3, 1, 2 }) d.File($"a_{i:0000}.png", "x");
        d.File("a_0001.jpg", "x");
        d.File("b.0001.png", "x");
        d.File("b.0002.png", "x");
        d.File("single_0001.png", "x");
        d.File("notes.txt", "x");

        var seqs = SequenceDetector.Scan(d.Path, maxDepth: 0);

        Assert.Equal(2, seqs.Count);
        var a = seqs.Single(s => s.Prefix == "a");
        Assert.Equal([1, 2, 3], a.Frames.Select(f => f.Number));
        Assert.Equal("a_####.png", a.DisplayName);
        Assert.Equal(0, a.MissingFrames);
        Assert.True(SequenceDetector.FolderHasSequence(d.Path));
    }

    [Fact]
    public void Counts_missing_frames()
    {
        var seq = SequenceDetector.Group([@"C:\x\s_0001.png", @"C:\x\s_0004.png"]).Single();
        Assert.Equal(2, seq.MissingFrames);
    }

    [Fact]
    public void Renumber_plan_renames_sequentially()
    {
        var seq = SequenceDetector.Group([@"C:\x\old_0010.png", @"C:\x\old_0020.png", @"C:\x\old_0030.png"]).Single();
        var plan = SequenceRenumberer.Plan(seq, new RenumberOptions("new", true, 1001, 4), RenumberMode.Copy, []);

        Assert.True(plan.CanRun);
        Assert.Equal(["new_1001.png", "new_1002.png", "new_1003.png"], plan.Items.Select(i => i.NewName));
    }

    [Fact]
    public void Renumber_plan_keeps_numbers_when_not_renumbering()
    {
        var seq = SequenceDetector.Group([@"C:\x\old_1.png", @"C:\x\old_2.png"]).Single();
        var plan = SequenceRenumberer.Plan(seq, new RenumberOptions(null, false, 0, 4), RenumberMode.Rename, []);
        Assert.Equal(["old_0001.png", "old_0002.png"], plan.Items.Select(i => i.NewName));
    }

    [Fact]
    public void Renumber_plan_detects_clash_with_other_files()
    {
        var seq = SequenceDetector.Group([@"C:\x\a_0001.png", @"C:\x\a_0002.png"]).Single();
        var plan = SequenceRenumberer.Plan(seq, new RenumberOptions("b", true, 1, 4), RenumberMode.Rename, ["a_0001.png", "a_0002.png", "b_0002.png"]);
        Assert.False(plan.CanRun);
        Assert.Single(plan.Errors);
    }

    [Fact]
    public void Renumber_plan_allows_overlap_with_own_files_in_rename_mode()
    {
        // 1,2 → 2,3: 새 이름 a_0002가 자기 시퀀스 파일이라 2단계 이름 바꾸기로 안전
        var seq = SequenceDetector.Group([@"C:\x\a_0001.png", @"C:\x\a_0002.png"]).Single();
        var plan = SequenceRenumberer.Plan(seq, new RenumberOptions(null, true, 2, 4), RenumberMode.Rename, ["a_0001.png", "a_0002.png"]);
        Assert.True(plan.CanRun);
    }

    [Fact]
    public void Renumber_plan_rejects_invalid_prefix_and_duplicates()
    {
        var seq = SequenceDetector.Group([@"C:\x\a_0001.png", @"C:\x\a_0010.png"]).Single();
        Assert.False(SequenceRenumberer.Plan(seq, new RenumberOptions("bad:name", true, 1, 4), RenumberMode.Copy, []).CanRun);
    }

    [Fact]
    public void Rename_mode_executes_with_two_passes()
    {
        using var d = new TempDir();
        var files = new[] { d.File("a_0001.png", "1"), d.File("a_0002.png", "2"), d.File("a_0003.png", "3") };
        var seq = SequenceDetector.Group(files).Single();
        var plan = SequenceRenumberer.Plan(seq, new RenumberOptions(null, true, 2, 4), RenumberMode.Rename, Directory.GetFiles(d.Path).Select(Path.GetFileName)!);

        var result = SequenceRenumberer.Execute(plan, RenumberMode.Rename);

        Assert.Equal(3, result.Succeeded);
        Assert.Empty(result.Errors);
        Assert.Equal("1", File.ReadAllText(Path.Combine(d.Path, "a_0002.png")));
        Assert.Equal("3", File.ReadAllText(Path.Combine(d.Path, "a_0004.png")));
        Assert.False(File.Exists(Path.Combine(d.Path, "a_0001.png")));
        Assert.Empty(Directory.GetFiles(d.Path, "*" + SequenceRenumberer.TempSuffix));
    }

    [Fact]
    public void Rename_mode_rolls_back_when_first_pass_fails()
    {
        using var d = new TempDir();
        var files = new[] { d.File("a_0001.png", "1"), d.File("a_0002.png", "2") };
        var seq = SequenceDetector.Group(files).Single();
        var plan = SequenceRenumberer.Plan(seq, new RenumberOptions("b", true, 1, 4), RenumberMode.Rename, []);

        // 두 번째 파일을 잠가 1단계를 실패시킨다
        using (new FileStream(files[1], FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = SequenceRenumberer.Execute(plan, RenumberMode.Rename);
            Assert.True(result.RolledBack);
        }

        Assert.True(File.Exists(files[0]));
        Assert.True(File.Exists(files[1]));
        Assert.Empty(Directory.GetFiles(d.Path, "*" + SequenceRenumberer.TempSuffix));
    }

    [Fact]
    public void Copy_mode_writes_to_renamed_folder()
    {
        using var d = new TempDir();
        var src = d.Dir("shots");
        var files = new[] { d.File(@"shots\a_0001.png", "1"), d.File(@"shots\a_0002.png", "2") };
        var seq = SequenceDetector.Group(files).Single();
        var plan = SequenceRenumberer.Plan(seq, new RenumberOptions("c", true, 101, 3), RenumberMode.Copy, []);

        var result = SequenceRenumberer.Execute(plan, RenumberMode.Copy);

        Assert.Equal(src + "_renamed", result.OutputFolder);
        Assert.Equal("2", File.ReadAllText(Path.Combine(src + "_renamed", "c_102.png")));
        Assert.True(File.Exists(files[0]));
        Directory.Delete(src + "_renamed", recursive: true);
    }
}

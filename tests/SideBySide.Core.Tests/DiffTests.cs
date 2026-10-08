using SideBySide.Core.Diff;

namespace SideBySide.Core.Tests;

public class DiffTests
{
    [Fact]
    public void Myers_ops_reconstruct_target_randomized()
    {
        var rnd = new Random(1234);
        for (int iter = 0; iter < 500; iter++)
        {
            var a = Enumerable.Range(0, rnd.Next(0, 40)).Select(_ => rnd.Next(0, 5)).ToArray();
            var b = Enumerable.Range(0, rnd.Next(0, 40)).Select(_ => rnd.Next(0, 5)).ToArray();
            var ops = Myers.Diff(a, b);

            var rebuilt = new List<int>();
            int ai = 0, bi = 0, equal = 0;
            foreach (var op in ops)
            {
                switch (op.Kind)
                {
                    case DiffOpKind.Equal:
                        Assert.Equal(ai, op.LeftStart);
                        Assert.Equal(bi, op.RightStart);
                        for (int k = 0; k < op.Length; k++) Assert.Equal(a[ai + k], b[bi + k]);
                        rebuilt.AddRange(a.Skip(ai).Take(op.Length));
                        ai += op.Length; bi += op.Length; equal += op.Length;
                        break;
                    case DiffOpKind.Delete:
                        ai += op.Length;
                        break;
                    case DiffOpKind.Insert:
                        rebuilt.AddRange(b.Skip(bi).Take(op.Length));
                        bi += op.Length;
                        break;
                }
            }

            Assert.Equal(a.Length, ai);
            Assert.Equal(b, rebuilt);
            Assert.Equal(Lcs(a, b), equal); // 최소 편집 = 최장 공통 부분열
        }
    }

    private static int Lcs(int[] a, int[] b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                dp[i, j] = a[i - 1] == b[j - 1] ? dp[i - 1, j - 1] + 1 : Math.Max(dp[i - 1, j], dp[i, j - 1]);
        return dp[a.Length, b.Length];
    }

    [Fact]
    public void TextDiff_builds_hunks_and_char_spans()
    {
        var left = new[] { "a", "b", "c", "d" };
        var right = new[] { "a", "B", "c", "d", "e" };
        var r = TextDiff.Compute(left, right);

        Assert.Equal(2, r.Hunks.Count);
        var changed = r.Lines.Single(l => l.Kind == DiffLineKind.Changed);
        Assert.Equal("b", changed.LeftText);
        Assert.Equal("B", changed.RightText);
        Assert.Equal(new TextSpan(0, 1), Assert.Single(changed.LeftSpans));
        Assert.Equal(DiffLineKind.RightOnly, r.Lines[^1].Kind);
    }

    [Fact]
    public void Ignore_options()
    {
        Assert.False(TextDiff.Compute(["a  b"], ["ab"]).AreEqual);
        Assert.True(TextDiff.Compute(["a  b"], ["ab"], new DiffOptions { IgnoreWhitespace = true }).AreEqual);
        Assert.True(TextDiff.Compute(["ABC"], ["abc"], new DiffOptions { IgnoreCase = true }).AreEqual);
    }

    [Fact]
    public void CharDiff_highlights_middle()
    {
        var (l, r) = TextDiff.CharDiff("int value = 10;", "int value = 200;");
        Assert.NotEmpty(r);
        Assert.All(l.Concat(r), s => Assert.True(s.Start >= 12));
    }

    [Fact]
    public void Document_apply_hunks_until_equal_and_undo()
    {
        var doc = new DiffDocument(["1", "2", "3", "x"], ["1", "two", "3", "y", "z"]);
        Assert.Equal(2, doc.Result.Hunks.Count);

        doc.ApplyHunk(0, DiffSide.Right);
        Assert.True(doc.RightDirty);
        Assert.Single(doc.Result.Hunks);
        doc.ApplyHunk(0, DiffSide.Right);
        Assert.True(doc.Result.AreEqual);
        Assert.Equal(doc.Left, doc.Right);

        Assert.True(doc.Undo());
        Assert.Single(doc.Result.Hunks);
        Assert.True(doc.Undo());
        Assert.False(doc.RightDirty);
        Assert.Equal(["1", "two", "3", "y", "z"], doc.Right);
    }

    [Fact]
    public void Document_find_hunk_navigation()
    {
        var doc = new DiffDocument(["a", "x", "b", "c", "y"], ["a", "b", "c"]);
        Assert.Equal(0, doc.FindHunk(-1, forward: true));
        Assert.Equal(1, doc.FindHunk(doc.Result.Hunks[0].FirstRow, forward: true));
        Assert.Equal(-1, doc.FindHunk(doc.Result.Hunks[1].FirstRow, forward: true));
        Assert.Equal(0, doc.FindHunk(doc.Result.Hunks[1].FirstRow, forward: false));
    }
}

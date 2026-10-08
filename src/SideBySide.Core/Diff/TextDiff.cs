namespace SideBySide.Core.Diff;

public enum DiffLineKind
{
    Same,
    Changed,
    LeftOnly,
    RightOnly,
}

/// <summary>문자 단위 강조 구간.</summary>
public readonly record struct TextSpan(int Start, int Length);

/// <summary>
/// 화면에 표시되는 정렬된 한 줄. 한쪽이 없으면 해당 쪽 번호/텍스트가 null.
/// 번호는 0부터 시작하는 원본 라인 인덱스.
/// </summary>
public sealed record DiffLine(
    DiffLineKind Kind,
    int? LeftIndex,
    int? RightIndex,
    string? LeftText,
    string? RightText,
    int HunkIndex,
    IReadOnlyList<TextSpan> LeftSpans,
    IReadOnlyList<TextSpan> RightSpans);

/// <summary>연속된 차이 묶음. 병합(블록 적용)의 단위.</summary>
public sealed record DiffHunk(int Index, int LeftStart, int LeftCount, int RightStart, int RightCount, int FirstRow, int RowCount);

public sealed record DiffResult(IReadOnlyList<DiffLine> Lines, IReadOnlyList<DiffHunk> Hunks)
{
    public bool AreEqual => Hunks.Count == 0;
}

public static class TextDiff
{
    /// <summary>이 길이를 넘는 줄은 문자 단위 비교를 생략하고 공통 접두/접미만 잘라 강조한다.</summary>
    private const int MaxCharDiffLength = 2000;

    private static readonly IReadOnlyList<TextSpan> NoSpans = [];

    public static DiffResult Compute(IReadOnlyList<string> left, IReadOnlyList<string> right, DiffOptions? options = null)
    {
        options ??= new DiffOptions();
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var a = ToIds(left, ids, options);
        var b = ToIds(right, ids, options);
        var ops = Myers.Diff(a, b);

        var lines = new List<DiffLine>(Math.Max(left.Count, right.Count));
        var hunks = new List<DiffHunk>();

        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            if (op.Kind == DiffOpKind.Equal)
            {
                for (int k = 0; k < op.Length; k++)
                {
                    int li = op.LeftStart + k, ri = op.RightStart + k;
                    lines.Add(new DiffLine(DiffLineKind.Same, li, ri, left[li], right[ri], -1, NoSpans, NoSpans));
                }

                continue;
            }

            // 연속된 Delete/Insert를 하나의 hunk로 묶는다.
            int leftStart = op.LeftStart, rightStart = op.RightStart, leftCount = 0, rightCount = 0;
            while (i < ops.Count && ops[i].Kind != DiffOpKind.Equal)
            {
                if (ops[i].Kind == DiffOpKind.Delete) leftCount += ops[i].Length;
                else rightCount += ops[i].Length;
                i++;
            }

            i--;
            var hunkIndex = hunks.Count;
            var firstRow = lines.Count;
            var paired = Math.Min(leftCount, rightCount);
            for (int k = 0; k < Math.Max(leftCount, rightCount); k++)
            {
                if (k < paired)
                {
                    int li = leftStart + k, ri = rightStart + k;
                    var (ls, rs) = CharDiff(left[li], right[ri]);
                    lines.Add(new DiffLine(DiffLineKind.Changed, li, ri, left[li], right[ri], hunkIndex, ls, rs));
                }
                else if (k < leftCount)
                {
                    var li = leftStart + k;
                    lines.Add(new DiffLine(DiffLineKind.LeftOnly, li, null, left[li], null, hunkIndex, NoSpans, NoSpans));
                }
                else
                {
                    var ri = rightStart + k;
                    lines.Add(new DiffLine(DiffLineKind.RightOnly, null, ri, null, right[ri], hunkIndex, NoSpans, NoSpans));
                }
            }

            hunks.Add(new DiffHunk(hunkIndex, leftStart, leftCount, rightStart, rightCount, firstRow, lines.Count - firstRow));
        }

        return new DiffResult(lines, hunks);
    }

    /// <summary>두 줄 사이의 문자 단위 변경 구간.</summary>
    public static (IReadOnlyList<TextSpan> Left, IReadOnlyList<TextSpan> Right) CharDiff(string left, string right)
    {
        int prefix = 0;
        int max = Math.Min(left.Length, right.Length);
        while (prefix < max && left[prefix] == right[prefix]) prefix++;
        int suffix = 0;
        while (suffix < max - prefix && left[left.Length - 1 - suffix] == right[right.Length - 1 - suffix]) suffix++;

        var lMid = left.AsSpan(prefix, left.Length - prefix - suffix);
        var rMid = right.AsSpan(prefix, right.Length - prefix - suffix);

        if (lMid.Length + rMid.Length > MaxCharDiffLength || lMid.Length == 0 || rMid.Length == 0)
        {
            return (
                lMid.Length > 0 ? [new TextSpan(prefix, lMid.Length)] : NoSpans,
                rMid.Length > 0 ? [new TextSpan(prefix, rMid.Length)] : NoSpans);
        }

        var a = new int[lMid.Length];
        var b = new int[rMid.Length];
        for (int k = 0; k < a.Length; k++) a[k] = lMid[k];
        for (int k = 0; k < b.Length; k++) b[k] = rMid[k];

        var ls = new List<TextSpan>();
        var rs = new List<TextSpan>();
        foreach (var op in Myers.Diff(a, b))
        {
            if (op.Kind == DiffOpKind.Delete) ls.Add(new TextSpan(prefix + op.LeftStart, op.Length));
            else if (op.Kind == DiffOpKind.Insert) rs.Add(new TextSpan(prefix + op.RightStart, op.Length));
        }

        return (ls, rs);
    }

    private static int[] ToIds(IReadOnlyList<string> lines, Dictionary<string, int> ids, DiffOptions options)
    {
        var result = new int[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            var key = Normalize(lines[i], options);
            if (!ids.TryGetValue(key, out var id))
            {
                id = ids.Count;
                ids[key] = id;
            }

            result[i] = id;
        }

        return result;
    }

    private static string Normalize(string line, DiffOptions options)
    {
        if (options.IgnoreWhitespace)
            line = string.Concat(line.Where(c => !char.IsWhiteSpace(c)));
        if (options.IgnoreCase)
            line = line.ToUpperInvariant();
        return line;
    }
}

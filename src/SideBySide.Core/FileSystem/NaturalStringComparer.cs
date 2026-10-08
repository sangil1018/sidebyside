namespace SideBySide.Core.FileSystem;

/// <summary>
/// Windows 탐색기와 같은 "자연 정렬": "file2" &lt; "file10". 대소문자 무시.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (si < x.Length && x[si] == '0') si++;
                while (sj < y.Length && y[sj] == '0') sj++;
                int ei = si, ej = sj;
                while (ei < x.Length && char.IsDigit(x[ei])) ei++;
                while (ej < y.Length && char.IsDigit(y[ej])) ej++;

                var lenDiff = (ei - si) - (ej - sj);
                if (lenDiff != 0) return lenDiff;

                var cmp = string.CompareOrdinal(x, si, y, sj, ei - si);
                if (cmp != 0) return cmp;

                // 값이 같으면 앞자리 0이 적은 쪽이 먼저
                var zeros = (si - i) - (sj - j);
                if (zeros != 0) return zeros;

                i = ei;
                j = ej;
                continue;
            }

            var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (c != 0) return c;
            i++;
            j++;
        }

        return (x.Length - i) - (y.Length - j);
    }
}

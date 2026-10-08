namespace SideBySide.Core.Diff;

public enum DiffOpKind
{
    Equal,
    Delete,
    Insert,
}

/// <summary>연속 구간 하나. Delete는 왼쪽만, Insert는 오른쪽만 진행한다.</summary>
public readonly record struct DiffOp(DiffOpKind Kind, int LeftStart, int RightStart, int Length);

/// <summary>
/// Myers O(ND) 차이 알고리즘의 선형 메모리 버전 (middle snake 분할 정복).
/// 정수 시퀀스(라인 해시 ID 또는 문자)를 비교한다.
/// </summary>
public static class Myers
{
    public static List<DiffOp> Diff(ReadOnlySpan<int> a, ReadOnlySpan<int> b)
    {
        var aMatched = new bool[a.Length];
        var bMatched = new bool[b.Length];
        var ctx = new Context(a.ToArray(), b.ToArray(), aMatched, bMatched);
        ctx.Run(0, a.Length, 0, b.Length);
        return BuildOps(aMatched, bMatched);
    }

    private static List<DiffOp> BuildOps(bool[] aMatched, bool[] bMatched)
    {
        var ops = new List<DiffOp>();
        int i = 0, j = 0;
        while (i < aMatched.Length || j < bMatched.Length)
        {
            if (i < aMatched.Length && !aMatched[i])
            {
                var s = i;
                while (i < aMatched.Length && !aMatched[i]) i++;
                ops.Add(new DiffOp(DiffOpKind.Delete, s, j, i - s));
            }
            else if (j < bMatched.Length && !bMatched[j])
            {
                var s = j;
                while (j < bMatched.Length && !bMatched[j]) j++;
                ops.Add(new DiffOp(DiffOpKind.Insert, i, s, j - s));
            }
            else
            {
                int si = i, sj = j;
                while (i < aMatched.Length && j < bMatched.Length && aMatched[i] && bMatched[j]) { i++; j++; }
                ops.Add(new DiffOp(DiffOpKind.Equal, si, sj, i - si));
            }
        }

        return ops;
    }

    private sealed class Context(int[] a, int[] b, bool[] aMatched, bool[] bMatched)
    {
        private int[] _vf = [];
        private int[] _vb = [];

        public void Run(int aLo, int aHi, int bLo, int bHi)
        {
            while (true)
            {
                // 공통 접두/접미 제거
                while (aLo < aHi && bLo < bHi && a[aLo] == b[bLo]) { aMatched[aLo++] = true; bMatched[bLo++] = true; }
                while (aLo < aHi && bLo < bHi && a[aHi - 1] == b[bHi - 1]) { aMatched[--aHi] = true; bMatched[--bHi] = true; }

                if (aLo == aHi || bLo == bHi) return;

                var (x, y, u, v) = MiddleSnake(aLo, aHi, bLo, bHi);

                // 안전장치: 분할이 진행되지 않으면 나머지는 전부 변경으로 둔다.
                if ((x == aLo && y == bLo && u == aHi && v == bHi) || (x == aHi && y == bHi) || (u == aLo && v == bLo))
                    return;

                for (int k = 0; k < u - x; k++) { aMatched[x + k] = true; bMatched[y + k] = true; }

                Run(aLo, x, bLo, y);
                aLo = u;
                bLo = v;
            }
        }

        private (int X, int Y, int U, int V) MiddleSnake(int aLo, int aHi, int bLo, int bHi)
        {
            int n = aHi - aLo, m = bHi - bLo;
            int delta = n - m;
            bool odd = (delta & 1) != 0;
            int max = (n + m + 1) / 2;
            int offset = max + 1;
            int size = 2 * max + 3;
            if (_vf.Length < size) { _vf = new int[size]; _vb = new int[size]; }
            Array.Fill(_vf, -1, 0, size);
            Array.Fill(_vb, -1, 0, size);
            _vf[offset + 1] = 0;
            _vb[offset + 1] = 0;

            for (int d = 0; d <= max; d++)
            {
                for (int k = -d; k <= d; k += 2)
                {
                    int x = (k == -d || (k != d && _vf[offset + k - 1] < _vf[offset + k + 1]))
                        ? _vf[offset + k + 1]
                        : _vf[offset + k - 1] + 1;
                    int y = x - k;
                    int x0 = x, y0 = y;
                    while (x < n && y < m && a[aLo + x] == b[bLo + y]) { x++; y++; }
                    _vf[offset + k] = x;

                    int c = delta - k;
                    if (odd && c >= -(d - 1) && c <= d - 1 && _vb[offset + c] >= 0 && x + _vb[offset + c] >= n)
                        return (aLo + x0, bLo + y0, aLo + x, bLo + y);
                }

                for (int k = -d; k <= d; k += 2)
                {
                    int x = (k == -d || (k != d && _vb[offset + k - 1] < _vb[offset + k + 1]))
                        ? _vb[offset + k + 1]
                        : _vb[offset + k - 1] + 1;
                    int y = x - k;
                    int x0 = x, y0 = y;
                    while (x < n && y < m && a[aHi - 1 - x] == b[bHi - 1 - y]) { x++; y++; }
                    _vb[offset + k] = x;

                    int c = delta - k;
                    if (!odd && c >= -d && c <= d && _vf[offset + c] >= 0 && x + _vf[offset + c] >= n)
                        return (aLo + n - x, bLo + m - y, aLo + n - x0, bLo + m - y0);
                }
            }

            return (aLo, bLo, aHi, bHi);
        }
    }
}

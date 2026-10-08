namespace SideBySide.Core.Diff;

public enum DiffSide
{
    Left,
    Right,
}

/// <summary>
/// 파일 비교/병합 편집 모델. 양쪽 라인 목록을 들고 있고, 블록 적용·전체 텍스트 편집·실행 취소를 지원한다.
/// 변경할 때마다 차이를 다시 계산한다 (일반적인 소스 파일 크기에서는 수 ms).
/// </summary>
public sealed class DiffDocument
{
    private readonly Stack<Snapshot> _undo = new();
    private List<string> _left;
    private List<string> _right;

    public DiffDocument(IEnumerable<string> left, IEnumerable<string> right, DiffOptions? options = null)
    {
        _left = [.. left];
        _right = [.. right];
        Options = options ?? new DiffOptions();
        Result = TextDiff.Compute(_left, _right, Options);
    }

    public IReadOnlyList<string> Left => _left;

    public IReadOnlyList<string> Right => _right;

    public DiffOptions Options { get; private set; }

    public DiffResult Result { get; private set; }

    public bool LeftDirty { get; private set; }

    public bool RightDirty { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public event EventHandler? Changed;

    public void SetOptions(DiffOptions options)
    {
        Options = options;
        Recompute();
    }

    /// <summary>hunk를 반대편에서 <paramref name="target"/> 쪽으로 복사한다.</summary>
    public void ApplyHunk(int hunkIndex, DiffSide target)
    {
        if (hunkIndex < 0 || hunkIndex >= Result.Hunks.Count) return;
        var h = Result.Hunks[hunkIndex];
        PushUndo();

        if (target == DiffSide.Right)
        {
            _right.RemoveRange(h.RightStart, h.RightCount);
            _right.InsertRange(h.RightStart, _left.GetRange(h.LeftStart, h.LeftCount));
            RightDirty = true;
        }
        else
        {
            _left.RemoveRange(h.LeftStart, h.LeftCount);
            _left.InsertRange(h.LeftStart, _right.GetRange(h.RightStart, h.RightCount));
            LeftDirty = true;
        }

        Recompute();
    }

    /// <summary>모든 차이를 한쪽으로 적용 (= 반대편 내용으로 덮어쓰기).</summary>
    public void ApplyAll(DiffSide target)
    {
        if (Result.AreEqual) return;
        PushUndo();
        if (target == DiffSide.Right) { _right = [.. _left]; RightDirty = true; }
        else { _left = [.. _right]; LeftDirty = true; }
        Recompute();
    }

    /// <summary>편집 모드에서 수정한 전체 텍스트를 반영.</summary>
    public void ReplaceText(DiffSide side, IReadOnlyList<string> lines)
    {
        var current = side == DiffSide.Left ? _left : _right;
        if (current.SequenceEqual(lines, StringComparer.Ordinal)) return;

        PushUndo();
        if (side == DiffSide.Left) { _left = [.. lines]; LeftDirty = true; }
        else { _right = [.. lines]; RightDirty = true; }
        Recompute();
    }

    public bool Undo()
    {
        if (!_undo.TryPop(out var s)) return false;
        _left = s.Left;
        _right = s.Right;
        LeftDirty = s.LeftDirty;
        RightDirty = s.RightDirty;
        Recompute();
        return true;
    }

    public void MarkSaved(DiffSide side)
    {
        if (side == DiffSide.Left) LeftDirty = false;
        else RightDirty = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>row 위치 이후(또는 이전)의 hunk 인덱스. 없으면 -1.</summary>
    public int FindHunk(int fromRow, bool forward)
    {
        var hunks = Result.Hunks;
        if (forward)
        {
            foreach (var h in hunks)
                if (h.FirstRow > fromRow) return h.Index;
        }
        else
        {
            for (int i = hunks.Count - 1; i >= 0; i--)
                if (hunks[i].FirstRow < fromRow) return hunks[i].Index;
        }

        return -1;
    }

    private void PushUndo() => _undo.Push(new Snapshot([.. _left], [.. _right], LeftDirty, RightDirty));

    private void Recompute()
    {
        Result = TextDiff.Compute(_left, _right, Options);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed record Snapshot(List<string> Left, List<string> Right, bool LeftDirty, bool RightDirty);
}

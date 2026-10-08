namespace SideBySide.App.Services;

/// <summary>실행 취소 가능한 파일 작업 하나.</summary>
public sealed record UndoAction(string Description, Func<Task<bool>> Undo);

/// <summary>
/// 이 앱에서 한 파일 작업(이름 바꾸기, 이동, 복사, 새로 만들기, 휴지통 삭제)의 실행 취소 스택.
/// 영구 삭제와 다른 프로그램에서 한 작업은 대상이 아니다.
/// </summary>
public sealed class UndoService
{
    private const int Capacity = 50;
    private readonly LinkedList<UndoAction> _stack = new();

    public bool CanUndo => _stack.Count > 0;

    public string? NextDescription => _stack.First?.Value.Description;

    public event EventHandler? Changed;

    public void Push(UndoAction action)
    {
        _stack.AddFirst(action);
        while (_stack.Count > Capacity) _stack.RemoveLast();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Push(string description, Func<Task<bool>> undo) => Push(new UndoAction(description, undo));

    /// <summary>가장 최근 작업을 되돌린다. (설명, 성공 여부), 되돌릴 게 없으면 null.</summary>
    public async Task<(string Description, bool Succeeded)?> UndoAsync()
    {
        if (_stack.First is not { } node) return null;
        _stack.RemoveFirst();
        Changed?.Invoke(this, EventArgs.Empty);

        bool ok;
        try
        {
            ok = await node.Value.Undo();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ok = false;
        }

        return (node.Value.Description, ok);
    }
}

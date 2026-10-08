using System.Collections.ObjectModel;
using System.Windows.Threading;
using SideBySide.App.ViewModels;
using SideBySide.Core.FileSystem;

namespace SideBySide.App.Services;

/// <summary>
/// 파일 작업을 실행하고 진행 상황을 <see cref="Operations"/>로 노출한다 (하단 작업 줄 · 주소 표시줄 · 작업 표시줄).
/// 복사/이동은 Core의 <see cref="FileTransfer"/>를 스레드 풀에서 돌리고, 충돌 질문만 UI 스레드에서 대화상자로 묻는다.
/// </summary>
public sealed class FileTransferService(IDialogService dialogs)
{
    private static readonly TimeSpan KeepFinished = TimeSpan.FromSeconds(1.5);
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public ObservableCollection<OperationViewModel> Operations { get; } = [];

    public bool IsBusy => Operations.Any(o => !o.IsDone);

    public async Task<TransferResult> TransferAsync(TransferKind kind, IReadOnlyList<string> sources, string destination)
    {
        using var cts = new CancellationTokenSource();
        var title = kind == TransferKind.Copy ? "복사" : "이동";
        var op = new OperationViewModel($"{title} → {Path.GetFileName(Path.TrimEndingDirectorySeparator(destination))}", destination, cts);
        Operations.Add(op);

        // "나머지 모두에 적용"을 고르면 이후 충돌은 묻지 않는다
        ConflictChoice? applyToAll = null;
        async Task<ConflictChoice> ResolveAsync(TransferConflict conflict)
        {
            if (applyToAll is { } remembered) return conflict.CanOverwrite || remembered != ConflictChoice.Overwrite ? remembered : ConflictChoice.KeepBoth;
            var (choice, all) = await _dispatcher.InvokeAsync(() => dialogs.ResolveConflict(conflict));
            if (all) applyToAll = choice;
            return choice;
        }

        var progress = new Progress<TransferProgress>(op.Update);
        TransferResult result;
        try
        {
            result = await Task.Run(() => FileTransfer.RunAsync(kind, sources, destination, ResolveAsync, progress, cts.Token));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = new TransferResult([], 0, 0, [new TransferError(destination, ex.Message)], Cancelled: false);
        }

        op.Finish(Summary(title, result));
        _ = RemoveLaterAsync(op);

        if (result.Errors.Count > 0)
        {
            var lines = result.Errors.Take(10).Select(e => $"• {Path.GetFileName(e.Path)}: {e.Message}");
            var more = result.Errors.Count > 10 ? $"\n…외 {result.Errors.Count - 10}개" : string.Empty;
            dialogs.ShowError($"{result.Errors.Count}개 항목을 {title}하지 못했습니다.\n\n{string.Join("\n", lines)}{more}");
        }

        return result;
    }

    /// <summary>셸 작업처럼 진행률을 알 수 없는 작업 (휴지통 삭제 등).</summary>
    public async Task<bool> RunIndeterminateAsync(string title, string? directory, Func<Task<bool>> action)
    {
        var op = new OperationViewModel(title, directory, cts: null);
        Operations.Add(op);
        bool ok;
        try
        {
            ok = await action();
        }
        finally
        {
            op.Finish(string.Empty);
            _ = RemoveLaterAsync(op);
        }

        op.Details = ok ? "완료" : "취소되었거나 일부 실패";
        return ok;
    }

    private static string Summary(string title, TransferResult r)
    {
        if (r.Cancelled) return "취소됨";
        var text = $"{title} 완료 · {r.FilesProcessed - r.Skipped:N0}개";
        if (r.Skipped > 0) text += $" · 건너뜀 {r.Skipped:N0}";
        if (r.Errors.Count > 0) text += $" · 실패 {r.Errors.Count:N0}";
        return text;
    }

    private async Task RemoveLaterAsync(OperationViewModel op)
    {
        await Task.Delay(KeepFinished);
        Operations.Remove(op);
    }
}

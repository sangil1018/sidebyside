using System.Collections.ObjectModel;
using System.Windows.Threading;
using SideBySide.App.ViewModels;
using SideBySide.Core.Compare;
using SideBySide.Core.FileSystem;
using SideBySide.Core.Localization;

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

    public Task<TransferResult> TransferAsync(TransferKind kind, IReadOnlyList<string> sources, string destination)
        => RunAsync(kind, destination, autoOverwrite: null,
            (resolve, progress, ct) => FileTransfer.RunAsync(kind, sources, destination, resolve, progress, ct));

    /// <summary>
    /// 비교 복사: 정해 둔 파일만 같은 상대 경로로 복사한다.
    /// 비교에서 원본이 더 최신이라 고른 덮어쓰기(<see cref="CompareCopyFile.Overwrites"/>)는 묻지 않고 덮어쓰고,
    /// 그 밖의 충돌(복사 사이에 대상이 생겼거나 종류가 다른 경우)만 대화상자로 묻는다.
    /// </summary>
    public Task<TransferResult> CopyCompareAsync(CompareCopyPlan plan, string destination)
    {
        var overwrites = plan.Files.Where(f => f.Overwrites)
            .Select(f => Path.GetFullPath(f.Target))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = plan.Files.Select(f => (f.Source, f.Target)).ToList();
        return RunAsync(TransferKind.Copy, destination,
            conflict => conflict.CanOverwrite
                        && overwrites.Contains(Path.GetFullPath(conflict.DestinationPath))
                        && conflict.SourceModifiedUtc > conflict.DestinationModifiedUtc,
            (resolve, progress, ct) => FileTransfer.CopyFilesAsync(files, plan.Directories, destination, resolve, progress, ct));
    }

    private async Task<TransferResult> RunAsync(
        TransferKind kind, string destination, Func<TransferConflict, bool>? autoOverwrite,
        Func<Func<TransferConflict, Task<ConflictChoice>>, IProgress<TransferProgress>, CancellationToken, Task<TransferResult>> run)
    {
        using var cts = new CancellationTokenSource();
        var title = kind == TransferKind.Copy ? L.T("복사") : L.T("이동");
        var op = new OperationViewModel($"{title} → {Path.GetFileName(Path.TrimEndingDirectorySeparator(destination))}", destination, cts);
        Operations.Add(op);

        // "나머지 모두에 적용"을 고르면 이후 충돌은 묻지 않는다
        ConflictChoice? applyToAll = null;
        async Task<ConflictChoice> ResolveAsync(TransferConflict conflict)
        {
            if (autoOverwrite?.Invoke(conflict) == true) return ConflictChoice.Overwrite;
            if (applyToAll is { } remembered) return conflict.CanOverwrite || remembered != ConflictChoice.Overwrite ? remembered : ConflictChoice.KeepBoth;
            var (choice, all) = await _dispatcher.InvokeAsync(() => dialogs.ResolveConflict(conflict));
            if (all) applyToAll = choice;
            return choice;
        }

        var progress = new Progress<TransferProgress>(op.Update);
        TransferResult result;
        try
        {
            result = await Task.Run(() => run(ResolveAsync, progress, cts.Token));
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
            var more = result.Errors.Count > 10 ? L.F("\n…외 {0}개", result.Errors.Count - 10) : string.Empty;
            var format = kind == TransferKind.Copy ? "{0}개 항목을 복사하지 못했습니다.\n\n{1}{2}" : "{0}개 항목을 이동하지 못했습니다.\n\n{1}{2}";
            dialogs.ShowError(L.F(format, result.Errors.Count, string.Join("\n", lines), more));
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

        op.Details = ok ? L.T("완료") : L.T("취소되었거나 일부 실패");
        return ok;
    }

    private static string Summary(string title, TransferResult r)
    {
        if (r.Cancelled) return L.T("취소됨");
        var text = L.F("{0} 완료 · {1:N0}개", title, r.FilesProcessed - r.Skipped);
        if (r.Skipped > 0) text += L.F(" · 건너뜀 {0:N0}", r.Skipped);
        if (r.Errors.Count > 0) text += L.F(" · 실패 {0:N0}", r.Errors.Count);
        return text;
    }

    private async Task RemoveLaterAsync(OperationViewModel op)
    {
        await Task.Delay(KeepFinished);
        Operations.Remove(op);
    }
}

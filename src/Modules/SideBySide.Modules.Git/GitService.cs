using System.Text;
using Microsoft.VisualBasic.FileIO;
using VbFileSystem = Microsoft.VisualBasic.FileIO.FileSystem;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.Git;

/// <summary>git 작업이 실패했다 (메시지는 사용자에게 그대로 보여 준다).</summary>
internal sealed class GitException(string message, bool gitMissing = false) : Exception(message)
{
    public bool GitMissing { get; } = gitMissing;
}

/// <summary>
/// 저장소 하나에 대한 git 동작. GitHub Desktop처럼 스테이지를 따로 드러내지 않는다:
/// 커밋할 때 고른 파일만 스테이지에 올려 커밋하고, 변경 취소는 HEAD 상태로 되돌린다.
/// </summary>
internal sealed class GitService(string root)
{
    public string Root { get; } = root;

    public string FullPath(string relativePath) => Path.GetFullPath(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    // ── 조회 ────────────────────────────────────────────────────────

    public async Task<GitStatus> StatusAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all").ConfigureAwait(false);
        return GitParsers.ParseStatus(result.Output);
    }

    public async Task<IReadOnlyList<GitBranch>> BranchesAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "branch", "-a", "--format=%(HEAD)%09%(refname)").ConfigureAwait(false);
        return GitParsers.ParseBranches(result.Output);
    }

    /// <summary>작업 트리의 변경 하나를 HEAD와 비교한 diff.</summary>
    public async Task<IReadOnlyList<DiffLine>> DiffAsync(GitChange change, CancellationToken ct = default)
    {
        GitResult result;
        if (change.Kind == GitChangeKind.Untracked)
        {
            result = await NewFileDiffAsync(change.Path, ct).ConfigureAwait(false);
        }
        else if (change.Kind == GitChangeKind.Conflicted)
        {
            result = await GitCli.RunAsync(Root, ["diff", "--no-ext-diff", "--", change.Path], ct).ConfigureAwait(false);
        }
        else
        {
            var args = new List<string> { "diff", "HEAD", "--no-ext-diff", "-M", "--" };
            if (change.OldPath is not null) args.Add(change.OldPath);
            args.Add(change.Path);
            result = await GitCli.RunAsync(Root, args, ct).ConfigureAwait(false);
            // 첫 커밋 전에는 HEAD가 없다: 새 파일로 보여 준다
            if (!result.Ok && change.Kind == GitChangeKind.Added) result = await NewFileDiffAsync(change.Path, ct).ConfigureAwait(false);
        }

        ThrowIfMissing(result);
        return GitParsers.ParseDiff(result.Output);
    }

    public async Task<IReadOnlyList<GitCommit>> LogAsync(int skip, int count, CancellationToken ct = default)
    {
        var result = await GitCli.RunAsync(Root,
            ["log", "-n", count.ToString(System.Globalization.CultureInfo.InvariantCulture),
             "--skip=" + skip.ToString(System.Globalization.CultureInfo.InvariantCulture), GitParsers.LogFormat], ct).ConfigureAwait(false);
        ThrowIfMissing(result);
        // 커밋이 하나도 없는 저장소는 오류로 끝난다 → 빈 기록
        return result.Ok ? GitParsers.ParseLog(result.Output) : [];
    }

    /// <summary>아직 푸시하지 않은 커밋의 해시 (업스트림이 없으면 비어 있음).</summary>
    public async Task<IReadOnlySet<string>> UnpushedAsync(CancellationToken ct = default)
    {
        var result = await GitCli.RunAsync(Root, ["rev-list", "@{upstream}..HEAD"], ct).ConfigureAwait(false);
        return result.Ok
            ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>();
    }

    public async Task<IReadOnlyList<GitChange>> CommitFilesAsync(string hash, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "show", "--format=", "--name-status", "-z", "-M", "-m", "--first-parent", hash).ConfigureAwait(false);
        return GitParsers.ParseNameStatus(result.Output);
    }

    public async Task<IReadOnlyList<DiffLine>> CommitDiffAsync(string hash, GitChange change, CancellationToken ct = default)
    {
        var args = new List<string> { "show", "--format=", "--no-ext-diff", "-M", "-m", "--first-parent", hash, "--" };
        if (change.OldPath is not null) args.Add(change.OldPath);
        args.Add(change.Path);
        var result = await GitCli.RunAsync(Root, args, ct).ConfigureAwait(false);
        ThrowIfFailed(result);
        return GitParsers.ParseDiff(result.Output);
    }

    // ── 변경 ────────────────────────────────────────────────────────

    /// <summary>고른 변경만 커밋한다. 이전에 스테이지에 올려 둔 다른 변경은 내려놓는다 (작업 트리는 그대로).</summary>
    public async Task CommitAsync(IReadOnlyList<GitChange> changes, string summary, string description, CancellationToken ct = default)
    {
        if (changes.Count == 0) throw new GitException(L.T("커밋할 파일을 선택하세요."));

        // 스테이지를 비운 뒤 고른 파일만 올린다. 커밋이 없는 저장소에서는 reset이 실패할 수 있어 결과를 따지지 않는다.
        ThrowIfMissing(await GitCli.RunAsync(Root, ["reset", "-q"], ct).ConfigureAwait(false));

        var paths = new StringBuilder();
        foreach (var change in changes)
        {
            if (change.OldPath is not null) paths.Append(change.OldPath).Append('\0');
            paths.Append(change.Path).Append('\0');
        }

        ThrowIfFailed(await GitCli.RunAsync(Root, ["add", "-A", "--pathspec-from-file=-", "--pathspec-file-nul"], ct, paths.ToString()).ConfigureAwait(false));

        var args = new List<string> { "commit", "-q", "-m", summary };
        if (!string.IsNullOrWhiteSpace(description))
        {
            args.Add("-m");
            args.Add(description.Trim());
        }

        ThrowIfFailed(await GitCli.RunAsync(Root, args, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// 변경을 버린다: 추적 중인 파일은 HEAD 내용으로 되돌리고, 새로 생긴 파일은 휴지통으로 보낸다 (실수로 지워도 되살릴 수 있게).
    /// </summary>
    public async Task DiscardAsync(IReadOnlyList<GitChange> changes, CancellationToken ct = default)
    {
        if (changes.Count == 0) return;

        var all = new StringBuilder();
        var restore = new StringBuilder();
        var remove = new List<string>();
        foreach (var change in changes)
        {
            all.Append(change.Path).Append('\0');
            switch (change.Kind)
            {
                case GitChangeKind.Untracked:
                case GitChangeKind.Added:
                    remove.Add(change.Path);
                    break;
                case GitChangeKind.Renamed when change.OldPath is not null:
                    all.Append(change.OldPath).Append('\0');
                    restore.Append(change.OldPath).Append('\0');
                    remove.Add(change.Path);
                    break;
                default:
                    restore.Append(change.Path).Append('\0');
                    break;
            }
        }

        // 스테이지에서 내려놓기 (새 파일은 이 단계에서 추적 안 됨 상태가 된다). 커밋이 없는 저장소에서는 실패해도 된다.
        ThrowIfMissing(await GitCli.RunAsync(Root, ["reset", "-q", "--pathspec-from-file=-", "--pathspec-file-nul"], ct, all.ToString()).ConfigureAwait(false));

        if (restore.Length > 0)
        {
            ThrowIfFailed(await GitCli.RunAsync(Root,
                ["checkout", "HEAD", "--pathspec-from-file=-", "--pathspec-file-nul"], ct, restore.ToString()).ConfigureAwait(false));
        }

        if (remove.Count > 0) await Task.Run(() => Recycle(remove), ct).ConfigureAwait(false);
    }

    public async Task CheckoutAsync(GitBranch branch, CancellationToken ct = default)
    {
        var result = branch.IsRemote
            ? await GitCli.RunAsync(Root, ["checkout", "--track", branch.Name], ct).ConfigureAwait(false)
            : await GitCli.RunAsync(Root, ["checkout", branch.Name], ct).ConfigureAwait(false);
        ThrowIfFailed(result);
    }

    public async Task CreateBranchAsync(string name, CancellationToken ct = default)
        => ThrowIfFailed(await GitCli.RunAsync(Root, ["checkout", "-b", name], ct).ConfigureAwait(false));

    // ── 원격 ────────────────────────────────────────────────────────

    public async Task FetchAsync(CancellationToken ct = default)
        => ThrowIfFailed(await GitCli.RunAsync(Root, ["fetch", "--prune"], ct).ConfigureAwait(false));

    public async Task PullAsync(CancellationToken ct = default)
    {
        // 사용자가 pull 방식을 정해 두지 않았으면 병합으로 받는다 (정하지 않은 채 갈라진 브랜치를 받으면 git이 거부한다)
        var rebase = await GitCli.RunAsync(Root, ["config", "--get", "pull.rebase"], ct).ConfigureAwait(false);
        var fastForward = await GitCli.RunAsync(Root, ["config", "--get", "pull.ff"], ct).ConfigureAwait(false);
        ThrowIfMissing(rebase);
        var configured = (rebase.Ok && rebase.Output.Trim().Length > 0) || (fastForward.Ok && fastForward.Output.Trim().Length > 0);

        var args = new List<string> { "pull", "--no-edit" };
        if (!configured) args.Add("--no-rebase");
        ThrowIfFailed(await GitCli.RunAsync(Root, args, ct).ConfigureAwait(false));
    }

    /// <summary>푸시. 업스트림이 없으면 첫 원격(origin 우선)에 같은 이름으로 올리고 추적을 건다.</summary>
    public async Task PushAsync(GitStatus status, CancellationToken ct = default)
    {
        if (status.Upstream is not null)
        {
            ThrowIfFailed(await GitCli.RunAsync(Root, ["push"], ct).ConfigureAwait(false));
            return;
        }

        if (status.Branch is null) throw new GitException(L.T("브랜치가 없는 상태(detached HEAD)에서는 푸시할 수 없습니다."));
        var remote = await DefaultRemoteAsync(ct).ConfigureAwait(false)
                     ?? throw new GitException(L.T("이 저장소에는 원격 저장소가 없습니다."));
        ThrowIfFailed(await GitCli.RunAsync(Root, ["push", "-u", remote, status.Branch], ct).ConfigureAwait(false));
    }

    public async Task<string?> DefaultRemoteAsync(CancellationToken ct = default)
    {
        var result = await GitCli.RunAsync(Root, ["remote"], ct).ConfigureAwait(false);
        if (!result.Ok) return null;
        var remotes = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return remotes.Contains("origin") ? "origin" : remotes.FirstOrDefault();
    }

    // ── 내부 ────────────────────────────────────────────────────────

    private async Task<GitResult> RunAsync(CancellationToken ct, params string[] args)
    {
        var result = await GitCli.RunAsync(Root, args, ct).ConfigureAwait(false);
        ThrowIfFailed(result);
        return result;
    }

    /// <summary>추적하지 않는 파일을 "빈 파일과의 차이"로 보여 준다 (차이가 있으면 종료 코드 1 — 정상).</summary>
    private Task<GitResult> NewFileDiffAsync(string path, CancellationToken ct)
        => GitCli.RunAsync(Root, ["diff", "--no-index", "--no-ext-diff", "--", "/dev/null", path], ct);

    private void Recycle(IReadOnlyList<string> relativePaths)
    {
        foreach (var relative in relativePaths)
        {
            var path = FullPath(relative);
            try
            {
                if (File.Exists(path)) VbFileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                else if (Directory.Exists(path)) VbFileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                throw new GitException(L.F("'{0}'을(를) 휴지통으로 보내지 못했습니다.\n{1}", relative, ex.Message));
            }
        }
    }

    private static void ThrowIfFailed(GitResult result)
    {
        if (!result.Ok) throw new GitException(result.Message, result.GitMissing);
    }

    private static void ThrowIfMissing(GitResult result)
    {
        if (result.GitMissing) throw new GitException(result.Message, gitMissing: true);
    }
}

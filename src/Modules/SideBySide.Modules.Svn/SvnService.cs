using System.Globalization;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using SideBySide.Core.Localization;
using VbFileSystem = Microsoft.VisualBasic.FileIO.FileSystem;

namespace SideBySide.Modules.Svn;

/// <summary>svn 작업이 실패했다 (메시지는 사용자에게 그대로 보여 준다).</summary>
internal sealed class SvnException(string message, bool svnMissing = false) : Exception(message)
{
    public bool SvnMissing { get; } = svnMissing;
}

/// <summary>
/// 작업 사본 하나에 대한 svn 동작. Git 패널과 같은 흐름으로 쓰도록 add/delete를 따로 드러내지 않는다:
/// 커밋할 때 체크한 새 파일은 추가하고 없어진 파일은 지운 뒤 함께 커밋한다.
/// </summary>
internal sealed class SvnService(string root)
{
    /// <summary>새 파일 diff로 읽을 최대 크기 (그보다 크면 안내만).</summary>
    private const long MaxPreviewBytes = 2 * 1024 * 1024;

    public string Root { get; } = root;

    public string FullPath(string relativePath) => Path.GetFullPath(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    // ── 조회 ────────────────────────────────────────────────────────

    /// <param name="checkServer">서버의 새 리비전까지 확인한다 (<c>-u</c>, 네트워크 필요).</param>
    public async Task<SvnStatus> StatusAsync(bool checkServer, CancellationToken ct = default)
    {
        var args = new List<string> { "status", "--xml", "--ignore-externals" };
        if (checkServer) args.Add("-u");
        var result = await RunAsync(args, ct).ConfigureAwait(false);
        return Parse(() => SvnParsers.ParseStatus(result.Output));
    }

    public async Task<SvnInfo> InfoAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(["info", "--xml"], ct).ConfigureAwait(false);
        return Parse(() => SvnParsers.ParseInfo(result.Output));
    }

    /// <summary>표준 배치일 때 trunk와 branches 아래 폴더 (서버 조회).</summary>
    public async Task<IReadOnlyList<SvnBranch>> BranchesAsync(SvnLayout layout, CancellationToken ct = default)
    {
        var result = await SvnCli.RunAsync(Root, ["list", Caret(layout.Base + "/branches")], ct, SvnCli.NativeOutput).ConfigureAwait(false);
        ThrowIfMissing(result);
        // branches 폴더가 없는 저장소도 있다 → trunk만
        return SvnParsers.BuildBranches(layout, result.Ok ? SvnParsers.ParseList(result.Output) : []);
    }

    /// <summary>작업 사본의 변경 하나를 BASE와 비교한 diff.</summary>
    public async Task<IReadOnlyList<DiffLine>> DiffAsync(SvnChange change, CancellationToken ct = default)
    {
        switch (change.Kind)
        {
            case SvnChangeKind.Unversioned:
                return await Task.Run(() => NewFileLines(FullPath(change.Path)), ct).ConfigureAwait(false);
            case SvnChangeKind.Missing:
            {
                var cat = await SvnCli.RunAsync(Root, ["cat", "-r", "BASE", "--", change.Path], ct).ConfigureAwait(false);
                ThrowIfMissing(cat);
                return cat.Ok ? SvnParsers.WholeFile(cat.Output, added: false) : [new DiffLine(DiffLineKind.Note, cat.Message)];
            }

            default:
            {
                var result = await SvnCli.RunAsync(Root, ["diff", "--internal-diff", "--", change.Path], ct).ConfigureAwait(false);
                ThrowIfFailed(result);
                var lines = SvnParsers.ParseDiff(result.Output);
                // 기록 없이 추가한 폴더·이진 파일처럼 diff가 비면 새 파일 내용 또는 안내
                if (lines.Count == 0 && change.Kind == SvnChangeKind.Added) return await Task.Run(() => NewFileLines(FullPath(change.Path)), ct).ConfigureAwait(false);
                return lines;
            }
        }
    }

    /// <summary>
    /// 기록 한 쪽. <paramref name="before"/>가 있으면 그보다 이전 리비전부터.
    /// </summary>
    public async Task<IReadOnlyList<SvnRevision>> LogAsync(long? before, int count, CancellationToken ct = default)
    {
        var args = new List<string> { "log", "--xml", "-l", count.ToString(CultureInfo.InvariantCulture) };
        if (before is { } b)
        {
            if (b <= 1) return [];
            args.Add("-r");
            args.Add((b - 1).ToString(CultureInfo.InvariantCulture) + ":1");
        }

        var result = await RunAsync(args, ct).ConfigureAwait(false);
        return Parse(() => SvnParsers.ParseLog(result.Output));
    }

    public async Task<IReadOnlyList<SvnLogPath>> RevisionPathsAsync(long revision, CancellationToken ct = default)
    {
        var result = await RunAsync(["log", "--xml", "-v", "-r", Rev(revision)], ct).ConfigureAwait(false);
        return Parse(() => SvnParsers.ParseLog(result.Output)).FirstOrDefault()?.Paths ?? [];
    }

    /// <summary>리비전 하나에서 파일 하나가 바뀐 내용.</summary>
    public async Task<IReadOnlyList<DiffLine>> RevisionDiffAsync(long revision, SvnLogPath path, CancellationToken ct = default)
    {
        if (path.IsDirectory && path.Action is 'A' or 'D')
            return [new DiffLine(DiffLineKind.Note, path.Action == 'A' ? L.T("폴더를 추가했습니다.") : L.T("폴더를 지웠습니다."))];

        SvnResult result;
        if (path.Action == 'D')
        {
            // 지운 리비전에는 파일이 없다: 바로 앞 리비전의 내용을 모두 삭제로 보여 준다
            result = await SvnCli.RunAsync(Root, ["cat", Caret(path.RepositoryPath) + "@" + Rev(revision - 1)], ct).ConfigureAwait(false);
            ThrowIfFailed(result);
            return SvnParsers.WholeFile(result.Output, added: false);
        }

        result = await SvnCli.RunAsync(Root, ["diff", "--internal-diff", "-c", Rev(revision), Caret(path.RepositoryPath) + "@" + Rev(revision)], ct).ConfigureAwait(false);
        ThrowIfFailed(result);
        var lines = SvnParsers.ParseDiff(result.Output);
        return lines.Count > 0 ? lines : [new DiffLine(DiffLineKind.Note, L.T("내용 변경이 없습니다 (속성 또는 복사만 바뀜)."))];
    }

    // ── 변경 ────────────────────────────────────────────────────────

    /// <summary>체크한 변경만 커밋한다. 새 파일은 svn에 추가하고, 디스크에서 없어진 파일은 svn에서도 지운다.</summary>
    public async Task<long?> CommitAsync(IReadOnlyList<SvnChange> changes, string message, CancellationToken ct = default)
    {
        if (changes.Count == 0) throw new SvnException(L.T("커밋할 파일을 선택하세요."));

        var add = changes.Where(c => c.Kind == SvnChangeKind.Unversioned).Select(c => c.Path).ToList();
        var remove = changes.Where(c => c.Kind == SvnChangeKind.Missing).Select(c => c.Path).ToList();
        if (add.Count > 0) ThrowIfFailed(await WithTargetsAsync(["add", "--parents", "--force"], add, ct).ConfigureAwait(false));
        if (remove.Count > 0) ThrowIfFailed(await WithTargetsAsync(["delete", "--force"], remove, ct).ConfigureAwait(false));

        // 추가·삭제한 폴더는 안의 항목까지 함께 커밋돼야 하므로 depth는 기본값(infinity) 그대로
        var result = await WithTargetsAsync(["commit", "-m", message], changes.Select(c => c.Path).ToList(), ct).ConfigureAwait(false);
        ThrowIfFailed(result);

        // "Committed revision 12." (언어에 따라 문구가 다르므로 마지막 숫자만)
        var digits = new string(result.Output.Reverse().SkipWhile(c => !char.IsAsciiDigit(c)).TakeWhile(char.IsAsciiDigit).Reverse().ToArray());
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ? revision : null;
    }

    /// <summary>
    /// 변경을 버린다: 버전 관리 중인 파일은 BASE로 되돌리고, 새로 생긴 파일(추가 예약 포함)은 휴지통으로 보낸다.
    /// </summary>
    public async Task RevertAsync(IReadOnlyList<SvnChange> changes, CancellationToken ct = default)
    {
        if (changes.Count == 0) return;

        var revert = changes.Where(c => c.Kind != SvnChangeKind.Unversioned).Select(c => c.Path).ToList();
        var recycle = changes.Where(c => c.Kind is SvnChangeKind.Unversioned or SvnChangeKind.Added).Select(c => c.Path).ToList();

        if (revert.Count > 0) ThrowIfFailed(await WithTargetsAsync(["revert", "--depth", "infinity"], revert, ct).ConfigureAwait(false));
        if (recycle.Count > 0) await Task.Run(() => Recycle(recycle), ct).ConfigureAwait(false);
    }

    /// <summary>서버의 새 리비전을 받는다. 충돌은 표시만 하고 미뤄 둔다 (변경 목록에 '충돌'로 나온다).</summary>
    public async Task UpdateAsync(CancellationToken ct = default)
        => ThrowIfFailed(await SvnCli.RunAsync(Root, ["update", "--accept", "postpone"], ct).ConfigureAwait(false));

    public async Task SwitchAsync(SvnBranch branch, SvnLayout layout, CancellationToken ct = default)
        => ThrowIfFailed(await SvnCli.RunAsync(Root, ["switch", "--accept", "postpone", Caret(branch.Path + layout.SubPath)], ct).ConfigureAwait(false));

    /// <summary>지금 브랜치를 서버에서 branches/이름으로 복사(커밋 하나)한 뒤 그 브랜치로 전환한다.</summary>
    public async Task CreateBranchAsync(string name, SvnLayout layout, CancellationToken ct = default)
    {
        var target = layout.Base + "/branches/" + name;
        ThrowIfFailed(await SvnCli.RunAsync(Root,
            ["copy", "--parents", "-m", L.F("'{0}' 브랜치 만들기", name), Caret(layout.Branch), Caret(target)], ct).ConfigureAwait(false));
        ThrowIfFailed(await SvnCli.RunAsync(Root, ["switch", "--accept", "postpone", Caret(target + layout.SubPath)], ct).ConfigureAwait(false));
    }

    /// <summary>중단된 작업으로 잠긴 작업 사본을 푼다.</summary>
    public async Task CleanupAsync(CancellationToken ct = default)
        => ThrowIfFailed(await SvnCli.RunAsync(Root, ["cleanup"], ct).ConfigureAwait(false));

    // ── 내부 ────────────────────────────────────────────────────────

    private async Task<SvnResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await SvnCli.RunAsync(Root, args, ct).ConfigureAwait(false);
        ThrowIfFailed(result);
        return result;
    }

    /// <summary>경로 목록을 임시 파일(<c>--targets</c>)로 넘긴다 (명령줄 길이 제한·이름의 특수 문자 대비).</summary>
    private async Task<SvnResult> WithTargetsAsync(IReadOnlyList<string> args, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var file = Path.Combine(Path.GetTempPath(), "sidebyside-svn-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            // svn은 --targets 파일을 시스템 기본 인코딩으로 읽는다
            await File.WriteAllTextAsync(file, string.Join("\n", paths.Select(p => p.Replace('/', '\\'))) + "\n", SvnCli.NativeOutput, ct).ConfigureAwait(false);
            var all = new List<string>(args) { "--targets", file };
            return await SvnCli.RunAsync(Root, all, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 임시 폴더 정리는 시스템에 맡긴다
            }
        }
    }

    private static IReadOnlyList<DiffLine> NewFileLines(string path)
    {
        if (Directory.Exists(path)) return [new DiffLine(DiffLineKind.Note, L.T("새 폴더 (안의 파일까지 함께 추가됩니다)"))];
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return [];
            if (info.Length > MaxPreviewBytes) return [new DiffLine(DiffLineKind.Note, L.T("파일이 커서 내용을 표시하지 않습니다."))];
            var bytes = File.ReadAllBytes(path);
            if (Array.IndexOf(bytes, (byte)0) >= 0) return [new DiffLine(DiffLineKind.Note, L.T("이진 파일"))];
            return SvnParsers.WholeFile(Encoding.UTF8.GetString(bytes), added: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [new DiffLine(DiffLineKind.Note, ex.Message)];
        }
    }

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
                throw new SvnException(L.F("'{0}'을(를) 휴지통으로 보내지 못했습니다.\n{1}", relative, ex.Message));
            }
        }
    }

    /// <summary>저장소 경로 → svn이 작업 사본 기준으로 푸는 주소 (<c>^/trunk/a.cs</c>).</summary>
    private static string Caret(string repositoryPath) => "^/" + repositoryPath.TrimStart('/');

    private static string Rev(long revision) => revision.ToString(CultureInfo.InvariantCulture);

    private static T Parse<T>(Func<T> parse)
    {
        try
        {
            return parse();
        }
        catch (FormatException ex)
        {
            throw new SvnException(ex.Message);
        }
    }

    private static void ThrowIfFailed(SvnResult result)
    {
        if (!result.Ok) throw new SvnException(result.Message, result.SvnMissing);
    }

    private static void ThrowIfMissing(SvnResult result)
    {
        if (result.SvnMissing) throw new SvnException(result.Message, svnMissing: true);
    }
}

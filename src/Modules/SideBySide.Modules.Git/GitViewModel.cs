using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SideBySide.Core.FileSystem;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.Git;

/// <summary>설정에 저장하는 옵션.</summary>
public sealed class GitSettings
{
    /// <summary>한 창 모드에서 저장소 폴더에 들어가면 패널을 자동으로 연다 (앱의 ModuleManager가 읽는다).</summary>
    public bool AutoOpen { get; set; } = true;
}

/// <summary>변경 목록의 한 줄 (체크한 것만 커밋한다).</summary>
public sealed partial class ChangeItem : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked;

    public ChangeItem(GitChange change, bool isChecked)
    {
        Change = change;
        _isChecked = isChecked;
        var slash = change.Path.LastIndexOf('/');
        FileName = slash >= 0 ? change.Path[(slash + 1)..] : change.Path;
        FolderText = slash >= 0 ? "  " + change.Path[..slash] : string.Empty;
    }

    public GitChange Change { get; }

    public string FileName { get; }

    /// <summary>파일 이름 뒤에 흐리게 붙는 폴더 경로.</summary>
    public string FolderText { get; }

    public string PathText => Change.OldPath is null ? Change.Path : Change.OldPath + " → " + Change.Path;

    public string KindText => Change.Kind switch
    {
        GitChangeKind.Added => "A",
        GitChangeKind.Deleted => "D",
        GitChangeKind.Renamed => "R",
        GitChangeKind.Untracked => "?",
        GitChangeKind.Conflicted => "!",
        _ => "M",
    };

    public string KindTip => Change.Kind switch
    {
        GitChangeKind.Added => L.T("추가됨"),
        GitChangeKind.Deleted => L.T("삭제됨"),
        GitChangeKind.Renamed => L.T("이름 바뀜"),
        GitChangeKind.Untracked => L.T("새 파일"),
        GitChangeKind.Conflicted => L.T("충돌"),
        _ => L.T("수정됨"),
    };
}

/// <summary>기록 목록의 커밋 한 줄.</summary>
public sealed class CommitItem(GitCommit commit, bool isUnpushed)
{
    public GitCommit Commit { get; } = commit;

    /// <summary>아직 원격에 올리지 않은 커밋.</summary>
    public bool IsUnpushed { get; } = isUnpushed;

    public string Subject => Commit.Subject.Length > 0 ? Commit.Subject : L.T("(메시지 없음)");

    public string Meta => Commit.Author + " · " + GitViewModel.RelativeTime(Commit.Date) + " · " + Commit.ShortHash;

    public string Tip => Commit.Body.Length > 0 ? Commit.Subject + "\n\n" + Commit.Body : Commit.Subject;
}

/// <summary>Git 패널: 변경 목록·diff·커밋, 브랜치, 원격 동기화, 기록.</summary>
public sealed partial class GitViewModel : ObservableObject, IDisposable
{
    private const int HistoryPageSize = 100;

    private enum SyncAction
    {
        Fetch,
        Pull,
        Push,
        Publish,
    }

    private readonly IModuleHost _host;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private GitService? _git;
    private GitStatus _current = GitStatus.Empty;
    private SyncAction _syncAction;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _changeDiffCts;
    private CancellationTokenSource? _historyDiffCts;
    private int _refreshQueued;
    private bool _historyStale = true;
    private bool _updatingChecks;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepository), nameof(Subtitle))]
    private string? _repositoryRoot;

    /// <summary>패널 대신 보여 줄 안내 (저장소가 아님, git 없음, 상태를 읽지 못함).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private string? _problem;

    [ObservableProperty]
    private string _branchText = string.Empty;

    [ObservableProperty]
    private string _syncText = string.Empty;

    [ObservableProperty]
    private string _syncTip = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit), nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanges))]
    private bool _isHistory;

    [ObservableProperty]
    private ChangeItem? _selectedChange;

    [ObservableProperty]
    private IReadOnlyList<DiffLine> _changeDiff = [];

    [ObservableProperty]
    private string _changesTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _commitText = string.Empty;

    [ObservableProperty]
    private CommitItem? _selectedCommit;

    [ObservableProperty]
    private ChangeItem? _selectedCommitFile;

    [ObservableProperty]
    private IReadOnlyList<DiffLine> _historyDiff = [];

    [ObservableProperty]
    private bool _canLoadMore;

    [ObservableProperty]
    private bool _autoOpen;

    [ObservableProperty]
    private string _newBranchName = string.Empty;

    [ObservableProperty]
    private bool _isBranchPopupOpen;

    public GitViewModel(IModuleHost host)
    {
        _host = host;
        _autoOpen = host.LoadData<GitSettings>().AutoOpen;
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, OnRefreshTimer, _dispatcher);
        _refreshTimer.Stop();
        UpdateSync();
        UpdateChecks();
    }

    public ObservableCollection<GitBranch> Branches { get; } = [];

    public ObservableCollection<ChangeItem> Changes { get; } = [];

    public ObservableCollection<CommitItem> History { get; } = [];

    /// <summary>기록에서 고른 커밋의 변경 파일.</summary>
    public ObservableCollection<ChangeItem> CommitFiles { get; } = [];

    public bool HasRepository => RepositoryRoot is not null;

    public string Subtitle => Problem ?? RepositoryRoot ?? string.Empty;

    public bool IsChanges => !IsHistory;

    public bool IsIdle => !IsBusy;

    public bool CanCommit => !IsBusy && Summary.Trim().Length > 0 && Changes.Any(c => c.IsChecked);

    /// <summary>목록 머리글의 전체 선택. 일부만 체크돼 있으면 null.</summary>
    public bool? AllChecked
    {
        get
        {
            if (Changes.Count == 0) return false;
            var count = Changes.Count(c => c.IsChecked);
            return count == Changes.Count ? true : count == 0 ? false : null;
        }
        set
        {
            _updatingChecks = true;
            try
            {
                foreach (var item in Changes) item.IsChecked = value ?? false;
            }
            finally
            {
                _updatingChecks = false;
            }

            UpdateChecks();
        }
    }

    // ── 저장소 전환 · 새로 고침 ───────────────────────────────────────

    /// <summary>보여 줄 저장소를 바꾼다 (null이면 "저장소 아님" 안내).</summary>
    public void SetRepository(string? root)
    {
        if (_git is not null && GitRepository.SameRoot(root, RepositoryRoot))
        {
            ScheduleRefresh();
            return;
        }

        StopWatching();
        _changeDiffCts?.Cancel();
        _historyDiffCts?.Cancel();
        _git = root is null ? null : new GitService(root);
        _current = GitStatus.Empty;
        _historyStale = true;

        Changes.Clear();
        Branches.Clear();
        History.Clear();
        CommitFiles.Clear();
        ChangeDiff = [];
        HistoryDiff = [];
        Summary = string.Empty;
        Description = string.Empty;
        BranchText = string.Empty;
        CanLoadMore = false;
        IsBranchPopupOpen = false;
        RepositoryRoot = root;
        Problem = root is null ? L.T("이 폴더는 git 저장소가 아닙니다. 저장소 폴더를 연 뒤 다시 시도하세요.") : null;
        UpdateSync();
        UpdateChecks();

        if (root is null) return;
        StartWatching(root);
        _ = RefreshAsync();
    }

    /// <summary>잠시 뒤 상태를 다시 읽는다 (파일 변경·창 활성화가 몰려도 한 번만).</summary>
    public void ScheduleRefresh()
    {
        Interlocked.Exchange(ref _refreshQueued, 0);
        if (_disposed || _git is null) return;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    /// <summary>화면이 닫혔다: 감시와 진행 중인 조회를 멈춘다.</summary>
    public void Suspend()
    {
        _refreshTimer.Stop();
        StopWatching();
        _changeDiffCts?.Cancel();
        _historyDiffCts?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Suspend();
    }

    private void OnRefreshTimer(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_git is not { } git) return;
        await _gate.WaitAsync();
        try
        {
            await LoadAsync(git);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>사용자 동작 하나를 실행하고 상태를 다시 읽는다. 실패하면 git 메시지를 그대로 보여 준다.</summary>
    private async Task<bool> RunAsync(string status, Func<GitService, Task> action)
    {
        if (_git is not { } git) return false;
        await _gate.WaitAsync();
        IsBusy = true;
        Status = status;
        var ok = false;
        try
        {
            await action(git);
            ok = true;
        }
        catch (GitException ex)
        {
            if (ex.GitMissing) Problem = ex.Message;
            else _host.ShowError(ex.Message);
        }
        finally
        {
            await LoadAsync(git);
            IsBusy = false;
            Status = string.Empty;
            _gate.Release();
        }

        return ok;
    }

    private async Task LoadAsync(GitService git)
    {
        GitStatus status;
        IReadOnlyList<GitBranch> branches;
        try
        {
            status = await git.StatusAsync();
            branches = await git.BranchesAsync();
        }
        catch (GitException ex)
        {
            if (ReferenceEquals(git, _git)) Problem = ex.Message;
            return;
        }

        if (!ReferenceEquals(git, _git) || _disposed) return;

        Problem = null;
        _current = status;
        BranchText = status.Detached ? L.T("(분리된 HEAD)") : status.Branch ?? string.Empty;
        if (!Branches.SequenceEqual(branches))
        {
            Branches.Clear();
            foreach (var branch in branches) Branches.Add(branch);
        }

        ApplyChanges(status.Changes);
        UpdateSync();
        UpdateChecks();
        await LoadChangeDiffAsync();

        if (IsHistory) await LoadHistoryAsync(reset: true);
        else _historyStale = true;
    }

    private void ApplyChanges(IReadOnlyList<GitChange> changes)
    {
        ChangesTitle = L.F("변경된 파일 {0:N0}개", changes.Count);
        if (Changes.Select(c => c.Change).SequenceEqual(changes)) return;

        // 체크·선택은 경로 기준으로 이어 간다 (새로 나타난 파일은 체크된 상태로)
        var wasChecked = Changes.ToDictionary(c => c.Change.Path, c => c.IsChecked, StringComparer.Ordinal);
        var selected = SelectedChange?.Change.Path;

        Changes.Clear();
        foreach (var change in changes)
        {
            var item = new ChangeItem(change, !wasChecked.TryGetValue(change.Path, out var isChecked) || isChecked);
            item.PropertyChanged += OnChangeItemChanged;
            Changes.Add(item);
        }

        SelectedChange = Changes.FirstOrDefault(c => c.Change.Path == selected) ?? Changes.FirstOrDefault();
    }

    private void OnChangeItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_updatingChecks && e.PropertyName == nameof(ChangeItem.IsChecked)) UpdateChecks();
    }

    private void UpdateChecks()
    {
        var count = Changes.Count(c => c.IsChecked);
        var branch = BranchText.Length > 0 ? BranchText : "HEAD";
        CommitText = count > 0 ? L.F("{0}에 커밋 ({1:N0}개)", branch, count) : L.F("{0}에 커밋", branch);
        OnPropertyChanged(nameof(AllChecked));
        OnPropertyChanged(nameof(CanCommit));
    }

    private void UpdateSync()
    {
        var s = _current;
        if (s.Branch is not null && s.Upstream is null)
        {
            _syncAction = SyncAction.Publish;
            SyncText = L.T("브랜치 게시");
            SyncTip = L.T("이 브랜치를 원격 저장소에 올리고 추적을 설정합니다.");
        }
        else if (s.Behind > 0)
        {
            _syncAction = SyncAction.Pull;
            SyncText = s.Ahead > 0 ? L.F("Pull ↓{0} ↑{1}", s.Behind, s.Ahead) : L.F("Pull ↓{0}", s.Behind);
            SyncTip = L.F("원격의 새 커밋 {0:N0}개를 받습니다.", s.Behind);
        }
        else if (s.Ahead > 0)
        {
            _syncAction = SyncAction.Push;
            SyncText = L.F("Push ↑{0}", s.Ahead);
            SyncTip = L.F("내 커밋 {0:N0}개를 원격에 올립니다.", s.Ahead);
        }
        else
        {
            _syncAction = SyncAction.Fetch;
            SyncText = "Fetch";
            SyncTip = L.T("원격 저장소의 새 변경을 확인합니다.");
        }
    }

    // ── 변경 탭 ─────────────────────────────────────────────────────

    partial void OnSelectedChangeChanged(ChangeItem? value) => _ = LoadChangeDiffAsync();

    private async Task LoadChangeDiffAsync()
    {
        _changeDiffCts?.Cancel();
        var cts = _changeDiffCts = new CancellationTokenSource();
        if (_git is not { } git || SelectedChange is not { } item)
        {
            ChangeDiff = [];
            return;
        }

        try
        {
            var lines = await git.DiffAsync(item.Change, cts.Token);
            // 내용이 같으면 그대로 둔다 (자동 새로 고침 때 스크롤 위치가 튀지 않게)
            if (!cts.IsCancellationRequested && ReferenceEquals(item, SelectedChange) && !ChangeDiff.SequenceEqual(lines)) ChangeDiff = lines;
        }
        catch (OperationCanceledException)
        {
            // 다른 파일을 골랐다
        }
        catch (GitException ex)
        {
            ChangeDiff = [new DiffLine(DiffLineKind.Note, ex.Message)];
        }
    }

    [RelayCommand]
    private async Task CommitAsync()
    {
        var summary = Summary.Trim();
        var selected = Changes.Where(c => c.IsChecked).Select(c => c.Change).ToList();
        if (summary.Length == 0 || selected.Count == 0 || IsBusy) return;

        var description = Description;
        if (!await RunAsync(L.T("커밋하는 중…"), git => git.CommitAsync(selected, summary, description))) return;
        Summary = string.Empty;
        Description = string.Empty;
        _host.ShowNotice(L.F("커밋했습니다: {0}", summary));
    }

    [RelayCommand]
    private async Task DiscardAsync(ChangeItem? item)
    {
        item ??= SelectedChange;
        if (item is null || IsBusy) return;
        if (!_host.Confirm(L.F("'{0}'의 변경을 취소할까요?\n수정한 내용은 되돌릴 수 없고, 새로 만든 파일은 휴지통으로 이동합니다.", item.FileName), L.T("변경 취소")))
            return;
        await DiscardCoreAsync([item.Change]);
    }

    [RelayCommand]
    private async Task DiscardAllAsync()
    {
        if (Changes.Count == 0 || IsBusy) return;
        if (!_host.Confirm(L.F("변경된 파일 {0:N0}개를 모두 취소할까요?\n수정한 내용은 되돌릴 수 없고, 새로 만든 파일은 휴지통으로 이동합니다.", Changes.Count), L.T("모든 변경 취소")))
            return;
        await DiscardCoreAsync(Changes.Select(c => c.Change).ToList());
    }

    private async Task DiscardCoreAsync(IReadOnlyList<GitChange> changes)
    {
        if (await RunAsync(L.T("변경을 취소하는 중…"), git => git.DiscardAsync(changes))) RefreshExplorer();
    }

    /// <summary>파일 목록에서 이 파일을 보여 준다.</summary>
    [RelayCommand]
    private void Reveal(ChangeItem? item)
    {
        if (item is null || _git is not { } git) return;
        var path = git.FullPath(item.Change.Path);
        _host.NavigateTo(File.Exists(path) || Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? git.Root);
    }

    // ── 브랜치 · 원격 ───────────────────────────────────────────────

    [RelayCommand]
    private async Task CheckoutAsync(GitBranch? branch)
    {
        IsBranchPopupOpen = false;
        if (branch is null || branch.IsCurrent || IsBusy) return;
        if (await RunAsync(L.F("'{0}' 브랜치로 전환하는 중…", branch.Name), git => git.CheckoutAsync(branch))) RefreshExplorer();
    }

    [RelayCommand]
    private async Task CreateBranchAsync()
    {
        var name = NewBranchName.Trim();
        if (name.Length == 0 || IsBusy) return;
        if (name.Any(char.IsWhiteSpace))
        {
            _host.ShowError(L.T("브랜치 이름에는 공백을 쓸 수 없습니다."));
            return;
        }

        IsBranchPopupOpen = false;
        if (!await RunAsync(L.F("'{0}' 브랜치를 만드는 중…", name), git => git.CreateBranchAsync(name))) return;
        NewBranchName = string.Empty;
        _host.ShowNotice(L.F("'{0}' 브랜치를 만들고 전환했습니다.", name));
    }

    /// <summary>머리글의 동기화 단추: 상태에 따라 Fetch / Pull / Push / 브랜치 게시.</summary>
    [RelayCommand]
    private async Task SyncAsync()
    {
        if (IsBusy) return;
        var status = _current;
        switch (_syncAction)
        {
            case SyncAction.Pull:
                if (await RunAsync(L.T("원격에서 받는 중…"), git => git.PullAsync())) RefreshExplorer();
                break;
            case SyncAction.Push:
            case SyncAction.Publish:
                if (await RunAsync(L.T("원격에 올리는 중…"), git => git.PushAsync(status))) _host.ShowNotice(L.T("원격 저장소에 올렸습니다."));
                break;
            default:
                await RunAsync(L.T("원격 저장소를 확인하는 중…"), git => git.FetchAsync());
                break;
        }
    }

    /// <summary>새로 고침 단추: 원격까지 확인(Fetch)하고 상태를 다시 읽는다.</summary>
    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        if (IsBusy) return;
        if (_git is null)
        {
            // 저장소가 아니라는 안내 중: 지금 폴더로 다시 찾아본다
            SetRepository(GitRepository.FindRoot(_host.ActiveFolder));
            return;
        }

        await RunAsync(L.T("원격 저장소를 확인하는 중…"), git => git.FetchAsync());
    }

    private void RefreshExplorer()
    {
        if (RepositoryRoot is { } root) _host.RefreshFolder(root);
    }

    // ── 기록 탭 ─────────────────────────────────────────────────────

    partial void OnIsHistoryChanged(bool value)
    {
        if (value && _historyStale) _ = LoadHistoryAsync(reset: true);
    }

    [RelayCommand]
    private Task LoadMoreAsync() => LoadHistoryAsync(reset: false);

    private async Task LoadHistoryAsync(bool reset)
    {
        if (_git is not { } git) return;
        _historyStale = false;
        try
        {
            var commits = await git.LogAsync(reset ? 0 : History.Count, HistoryPageSize);
            var unpushed = await git.UnpushedAsync();
            if (!ReferenceEquals(git, _git)) return;

            var items = commits.Select(c => new CommitItem(c, unpushed.Contains(c.Hash))).ToList();
            if (reset)
            {
                // 첫 쪽이 그대로면 목록(과 더 불러온 쪽)을 유지한다
                var same = History.Count >= items.Count
                           && items.Select((item, i) => (item, i)).All(p => History[p.i].Commit.Hash == p.item.Commit.Hash && History[p.i].IsUnpushed == p.item.IsUnpushed)
                           && (items.Count == HistoryPageSize || History.Count == items.Count);
                if (same) return;

                var selected = SelectedCommit?.Commit.Hash;
                History.Clear();
                foreach (var item in items) History.Add(item);
                CanLoadMore = items.Count == HistoryPageSize;
                SelectedCommit = History.FirstOrDefault(c => c.Commit.Hash == selected) ?? History.FirstOrDefault();
            }
            else
            {
                foreach (var item in items) History.Add(item);
                CanLoadMore = items.Count == HistoryPageSize;
            }
        }
        catch (GitException ex)
        {
            if (ex.GitMissing) Problem = ex.Message;
        }
    }

    partial void OnSelectedCommitChanged(CommitItem? value) => _ = LoadCommitFilesAsync(value);

    private async Task LoadCommitFilesAsync(CommitItem? commit)
    {
        CommitFiles.Clear();
        HistoryDiff = [];
        if (commit is null || _git is not { } git) return;
        try
        {
            var files = await git.CommitFilesAsync(commit.Commit.Hash);
            if (!ReferenceEquals(commit, SelectedCommit)) return;
            foreach (var file in files) CommitFiles.Add(new ChangeItem(file, false));
            SelectedCommitFile = CommitFiles.FirstOrDefault();
        }
        catch (GitException ex)
        {
            HistoryDiff = [new DiffLine(DiffLineKind.Note, ex.Message)];
        }
    }

    partial void OnSelectedCommitFileChanged(ChangeItem? value) => _ = LoadHistoryDiffAsync(value);

    private async Task LoadHistoryDiffAsync(ChangeItem? file)
    {
        _historyDiffCts?.Cancel();
        var cts = _historyDiffCts = new CancellationTokenSource();
        if (file is null || SelectedCommit is not { } commit || _git is not { } git)
        {
            HistoryDiff = [];
            return;
        }

        try
        {
            var lines = await git.CommitDiffAsync(commit.Commit.Hash, file.Change, cts.Token);
            if (!cts.IsCancellationRequested && ReferenceEquals(file, SelectedCommitFile)) HistoryDiff = lines;
        }
        catch (OperationCanceledException)
        {
            // 다른 파일을 골랐다
        }
        catch (GitException ex)
        {
            HistoryDiff = [new DiffLine(DiffLineKind.Note, ex.Message)];
        }
    }

    // ── 설정 · 감시 ─────────────────────────────────────────────────

    partial void OnAutoOpenChanged(bool value) => _host.SaveData(new GitSettings { AutoOpen = value });

    private void StartWatching(string root)
    {
        try
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            watcher.Changed += OnFileSystemEvent;
            watcher.Created += OnFileSystemEvent;
            watcher.Deleted += OnFileSystemEvent;
            watcher.Renamed += OnFileSystemEvent;
            watcher.Error += (_, _) => QueueRefresh();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _watcher = null; // 감시할 수 없는 위치(일부 네트워크 드라이브): 창을 활성화할 때만 새로 고친다
        }
    }

    private void StopWatching()
    {
        if (_watcher is not { } watcher) return;
        _watcher = null;
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }

    // 감시 스레드에서 호출된다
    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (RepositoryRoot is { } root && IsRelevant(root, e.FullPath)) QueueRefresh();
    }

    private void QueueRefresh()
    {
        // 빌드처럼 이벤트가 몰릴 때 UI 스레드로 한 번만 넘긴다
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0) _dispatcher.BeginInvoke(new Action(ScheduleRefresh));
    }

    /// <summary>.git 안에서는 브랜치·스테이지가 바뀌는 파일만 본다 (git이 쓰는 임시 파일·객체는 무시).</summary>
    internal static bool IsRelevant(string root, string fullPath)
    {
        if (fullPath.Length <= root.Length) return false;
        var relative = fullPath[root.Length..].TrimStart('\\', '/');
        if (relative.Equals(".git", StringComparison.OrdinalIgnoreCase)) return false;
        if (!relative.StartsWith(".git\\", StringComparison.OrdinalIgnoreCase) && !relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))
            return true;

        var inner = relative[5..].Replace('/', '\\');
        return inner is "HEAD" or "index" or "ORIG_HEAD" or "MERGE_HEAD" or "FETCH_HEAD" or "packed-refs"
               || (inner.StartsWith("refs\\", StringComparison.OrdinalIgnoreCase) && !inner.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"3시간 전" 같은 상대 시각. 한 달이 넘으면 날짜.</summary>
    internal static string RelativeTime(DateTimeOffset time) => RelativeTime(time, DateTimeOffset.Now);

    internal static string RelativeTime(DateTimeOffset time, DateTimeOffset now)
    {
        var span = now - time;
        if (span < TimeSpan.FromMinutes(1)) return L.T("방금");
        if (span < TimeSpan.FromHours(1)) return L.F("{0}분 전", (int)span.TotalMinutes);
        if (span < TimeSpan.FromDays(1)) return L.F("{0}시간 전", (int)span.TotalHours);
        if (span < TimeSpan.FromDays(30)) return L.F("{0}일 전", (int)span.TotalDays);
        return time.LocalDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }
}

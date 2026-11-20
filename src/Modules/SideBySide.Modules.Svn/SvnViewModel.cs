using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SideBySide.Core.FileSystem;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.Svn;

/// <summary>설정에 저장하는 옵션.</summary>
public sealed class SvnSettings
{
    /// <summary>한 창 모드에서 작업 사본 폴더에 들어가면 패널을 자동으로 연다 (앱의 ModuleManager가 읽는다).</summary>
    public bool AutoOpen { get; set; } = true;
}

/// <summary>변경 목록의 한 줄 (체크한 것만 커밋한다).</summary>
public sealed partial class ChangeItem : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked;

    public ChangeItem(SvnChange change, bool isChecked)
    {
        Change = change;
        _isChecked = isChecked;
        (FileName, FolderText) = SplitPath(change.Path);
    }

    public SvnChange Change { get; }

    public string FileName { get; }

    /// <summary>파일 이름 뒤에 흐리게 붙는 폴더 경로.</summary>
    public string FolderText { get; }

    public string PathText => Change.Path;

    public string KindText => Change.Kind switch
    {
        SvnChangeKind.Added => "A",
        SvnChangeKind.Deleted => "D",
        SvnChangeKind.Missing => "!",
        SvnChangeKind.Unversioned => "?",
        SvnChangeKind.Conflicted => "C",
        _ => "M",
    };

    public string KindTip => Change.Kind switch
    {
        SvnChangeKind.Added => L.T("추가됨"),
        SvnChangeKind.Deleted => L.T("삭제됨"),
        SvnChangeKind.Missing => L.T("없어짐 (커밋하면 저장소에서도 지웁니다)"),
        SvnChangeKind.Unversioned => L.T("새 파일 (커밋하면 저장소에 추가합니다)"),
        SvnChangeKind.Conflicted => L.T("충돌"),
        _ => L.T("수정됨"),
    };

    internal static (string Name, string Folder) SplitPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? (trimmed[(slash + 1)..], "  " + trimmed[..slash]) : (trimmed, string.Empty);
    }
}

/// <summary>기록에서 고른 리비전의 바뀐 경로 한 줄.</summary>
public sealed class RevisionFileItem
{
    public RevisionFileItem(SvnLogPath path, string displayPath)
    {
        Path = path;
        PathText = displayPath;
        (FileName, FolderText) = ChangeItem.SplitPath(displayPath);
    }

    public SvnLogPath Path { get; }

    public string FileName { get; }

    public string FolderText { get; }

    public string PathText { get; }

    public string KindText => Path.Action switch
    {
        'A' => "A",
        'D' => "D",
        'R' => "R",
        _ => "M",
    };

    public string KindTip => Path.Action switch
    {
        'A' => L.T("추가됨"),
        'D' => L.T("삭제됨"),
        'R' => L.T("바뀜 (지우고 다시 추가)"),
        _ => L.T("수정됨"),
    };
}

/// <summary>기록 목록의 리비전 한 줄.</summary>
public sealed class RevisionItem(SvnRevision revision)
{
    public SvnRevision Revision { get; } = revision;

    public string Subject => Revision.Subject.Length > 0 ? Revision.Subject : L.T("(메시지 없음)");

    public string Meta => Revision.Author + " · " + SvnViewModel.RelativeTime(Revision.Date) + " · r" + Revision.Revision.ToString(CultureInfo.InvariantCulture);

    public string Tip => Revision.Body.Length > 0 ? Revision.Subject + "\n\n" + Revision.Body : Revision.Subject;
}

/// <summary>SVN 패널: 변경 목록·diff·커밋, 업데이트, 브랜치 전환, 기록.</summary>
public sealed partial class SvnViewModel : ObservableObject, IDisposable
{
    private const int HistoryPageSize = 100;

    private readonly IModuleHost _host;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SvnService? _svn;
    private SvnInfo? _info;
    private SvnLayout? _layout;
    private int? _incoming;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _changeDiffCts;
    private CancellationTokenSource? _historyDiffCts;
    private int _refreshQueued;
    private bool _historyStale = true;
    private bool _updatingChecks;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkingCopy), nameof(Subtitle))]
    private string? _workingCopyRoot;

    /// <summary>패널 대신 보여 줄 안내 (작업 사본이 아님, svn 없음, 상태를 읽지 못함).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private string? _problem;

    [ObservableProperty]
    private string _branchText = string.Empty;

    [ObservableProperty]
    private string _revisionText = string.Empty;

    [ObservableProperty]
    private string _syncText = "Update";

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
    private RevisionItem? _selectedRevision;

    [ObservableProperty]
    private RevisionFileItem? _selectedRevisionFile;

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

    /// <summary>브랜치 목록을 서버에서 읽는 중이거나 읽지 못했을 때의 안내.</summary>
    [ObservableProperty]
    private string? _branchMessage;

    public SvnViewModel(IModuleHost host)
    {
        _host = host;
        _autoOpen = host.LoadData<SvnSettings>().AutoOpen;
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, OnRefreshTimer, _dispatcher);
        _refreshTimer.Stop();
        UpdateSync();
        UpdateChecks();
    }

    public ObservableCollection<SvnBranch> Branches { get; } = [];

    public ObservableCollection<ChangeItem> Changes { get; } = [];

    public ObservableCollection<RevisionItem> History { get; } = [];

    /// <summary>기록에서 고른 리비전의 바뀐 경로.</summary>
    public ObservableCollection<RevisionFileItem> RevisionFiles { get; } = [];

    public bool HasWorkingCopy => WorkingCopyRoot is not null;

    public string Subtitle => Problem ?? WorkingCopyRoot ?? string.Empty;

    public bool IsChanges => !IsHistory;

    public bool IsIdle => !IsBusy;

    /// <summary>trunk / branches / tags 표준 배치라서 브랜치를 바꾸거나 만들 수 있다.</summary>
    public bool CanBranch => _layout is not null;

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

    // ── 작업 사본 전환 · 새로 고침 ─────────────────────────────────────

    /// <summary>보여 줄 작업 사본을 바꾼다 (null이면 "작업 사본 아님" 안내).</summary>
    public void SetWorkingCopy(string? root)
    {
        if (_svn is not null && SameRoot(root, WorkingCopyRoot))
        {
            ScheduleRefresh();
            return;
        }

        StopWatching();
        _changeDiffCts?.Cancel();
        _historyDiffCts?.Cancel();
        _svn = root is null ? null : new SvnService(root);
        _info = null;
        _layout = null;
        _incoming = null;
        _historyStale = true;

        Changes.Clear();
        Branches.Clear();
        History.Clear();
        RevisionFiles.Clear();
        ChangeDiff = [];
        HistoryDiff = [];
        Summary = string.Empty;
        Description = string.Empty;
        BranchText = string.Empty;
        RevisionText = string.Empty;
        CanLoadMore = false;
        IsBranchPopupOpen = false;
        WorkingCopyRoot = root;
        Problem = root is null ? L.T("이 폴더는 SVN 작업 사본이 아닙니다. 작업 사본 폴더를 연 뒤 다시 시도하세요.") : null;
        OnPropertyChanged(nameof(CanBranch));
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
        if (_disposed || _svn is null) return;
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

    private async Task RefreshAsync(bool checkServer = false)
    {
        if (_svn is not { } svn) return;
        await _gate.WaitAsync();
        try
        {
            await LoadAsync(svn, checkServer);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>사용자 동작 하나를 실행하고 상태를 다시 읽는다. 실패하면 svn 메시지를 그대로 보여 준다.</summary>
    private async Task<bool> RunAsync(string status, Func<SvnService, Task> action)
    {
        if (_svn is not { } svn) return false;
        await _gate.WaitAsync();
        IsBusy = true;
        Status = status;
        var ok = false;
        try
        {
            await action(svn);
            ok = true;
        }
        catch (SvnException ex)
        {
            if (ex.SvnMissing) Problem = ex.Message;
            else _host.ShowError(ex.Message);
        }
        finally
        {
            await LoadAsync(svn, checkServer: false);
            IsBusy = false;
            Status = string.Empty;
            _gate.Release();
        }

        return ok;
    }

    private async Task LoadAsync(SvnService svn, bool checkServer)
    {
        SvnStatus status;
        SvnInfo info;
        try
        {
            info = await svn.InfoAsync();
            status = await svn.StatusAsync(checkServer);
        }
        catch (SvnException ex)
        {
            if (ReferenceEquals(svn, _svn)) Problem = ex.Message;
            return;
        }

        if (!ReferenceEquals(svn, _svn) || _disposed) return;

        Problem = null;
        var switched = _info is not null && _info.RelativeUrl != info.RelativeUrl;
        _info = info;
        _layout = SvnParsers.ParseLayout(info.RelativeUrl);
        if (status.Incoming is { } incoming) _incoming = incoming;
        else if (switched) _incoming = null;

        BranchText = _layout?.Name ?? info.RelativeUrl;
        RevisionText = "r" + info.Revision.ToString(CultureInfo.InvariantCulture);
        if (switched) Branches.Clear();
        OnPropertyChanged(nameof(CanBranch));

        ApplyChanges(status.Changes);
        UpdateSync();
        UpdateChecks();
        await LoadChangeDiffAsync();

        if (switched) _historyStale = true;
        if (IsHistory) await LoadHistoryAsync(reset: true);
        else _historyStale = true;
    }

    private void ApplyChanges(IReadOnlyList<SvnChange> changes)
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
        var branch = BranchText.Length > 0 ? BranchText : "SVN";
        CommitText = count > 0 ? L.F("{0}에 커밋 ({1:N0}개)", branch, count) : L.F("{0}에 커밋", branch);
        OnPropertyChanged(nameof(AllChecked));
        OnPropertyChanged(nameof(CanCommit));
    }

    private void UpdateSync()
    {
        if (_incoming is > 0)
        {
            SyncText = L.F("Update ↓{0}", _incoming.Value);
            SyncTip = L.F("서버의 새 변경 {0:N0}건을 받습니다.", _incoming.Value);
        }
        else
        {
            SyncText = "Update";
            SyncTip = _incoming == 0 ? L.T("서버와 같은 상태입니다. 다시 받아 확인합니다.") : L.T("서버의 최신 리비전을 받습니다.");
        }
    }

    // ── 변경 탭 ─────────────────────────────────────────────────────

    partial void OnSelectedChangeChanged(ChangeItem? value) => _ = LoadChangeDiffAsync();

    private async Task LoadChangeDiffAsync()
    {
        _changeDiffCts?.Cancel();
        var cts = _changeDiffCts = new CancellationTokenSource();
        if (_svn is not { } svn || SelectedChange is not { } item)
        {
            ChangeDiff = [];
            return;
        }

        try
        {
            var lines = await svn.DiffAsync(item.Change, cts.Token);
            // 내용이 같으면 그대로 둔다 (자동 새로 고침 때 스크롤 위치가 튀지 않게)
            if (!cts.IsCancellationRequested && ReferenceEquals(item, SelectedChange) && !ChangeDiff.SequenceEqual(lines)) ChangeDiff = lines;
        }
        catch (OperationCanceledException)
        {
            // 다른 파일을 골랐다
        }
        catch (SvnException ex)
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

        var description = Description.Trim();
        var message = description.Length > 0 ? summary + "\n\n" + description : summary;
        long? revision = null;
        if (!await RunAsync(L.T("커밋하는 중…"), async svn => revision = await svn.CommitAsync(selected, message))) return;
        Summary = string.Empty;
        Description = string.Empty;
        _historyStale = true;
        _host.ShowNotice(revision is { } r
            ? L.F("r{0}로 커밋했습니다: {1}", r.ToString(CultureInfo.InvariantCulture), summary)
            : L.F("커밋했습니다: {0}", summary));
    }

    [RelayCommand]
    private async Task DiscardAsync(ChangeItem? item)
    {
        item ??= SelectedChange;
        if (item is null || IsBusy) return;
        if (!_host.Confirm(L.F("'{0}'의 변경을 취소할까요?\n수정한 내용은 되돌릴 수 없고, 새로 만든 파일은 휴지통으로 이동합니다.", item.FileName), L.T("변경 취소")))
            return;
        await RevertCoreAsync([item.Change]);
    }

    [RelayCommand]
    private async Task DiscardAllAsync()
    {
        if (Changes.Count == 0 || IsBusy) return;
        if (!_host.Confirm(L.F("변경된 파일 {0:N0}개를 모두 취소할까요?\n수정한 내용은 되돌릴 수 없고, 새로 만든 파일은 휴지통으로 이동합니다.", Changes.Count), L.T("모든 변경 취소")))
            return;
        await RevertCoreAsync(Changes.Select(c => c.Change).ToList());
    }

    private async Task RevertCoreAsync(IReadOnlyList<SvnChange> changes)
    {
        if (await RunAsync(L.T("변경을 취소하는 중…"), svn => svn.RevertAsync(changes))) RefreshExplorer();
    }

    /// <summary>파일 목록에서 이 파일을 보여 준다.</summary>
    [RelayCommand]
    private void Reveal(ChangeItem? item)
    {
        if (item is null || _svn is not { } svn) return;
        var path = svn.FullPath(item.Change.Path);
        _host.NavigateTo(File.Exists(path) || Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? svn.Root);
    }

    // ── 업데이트 · 브랜치 ───────────────────────────────────────────

    /// <summary>머리글의 Update 단추.</summary>
    [RelayCommand]
    private async Task SyncAsync()
    {
        if (IsBusy) return;
        if (!await RunAsync(L.T("서버에서 받는 중…"), svn => svn.UpdateAsync())) return;
        _incoming = 0;
        _historyStale = true;
        UpdateSync();
        RefreshExplorer();
        if (Changes.Any(c => c.Change.Kind == SvnChangeKind.Conflicted))
            _host.ShowError(L.T("업데이트 중 충돌이 생겼습니다. '충돌'로 표시된 파일을 정리한 뒤 커밋하세요."));
        else
            _host.ShowNotice(L.F("{0}(으)로 업데이트했습니다.", RevisionText));
    }

    /// <summary>새로 고침 단추: 서버의 새 변경까지 확인하고 상태를 다시 읽는다.</summary>
    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        if (IsBusy) return;
        if (_svn is null)
        {
            // 작업 사본이 아니라는 안내 중: 지금 폴더로 다시 찾아본다
            SetWorkingCopy(SvnWorkingCopy.FindRoot(_host.ActiveFolder));
            return;
        }

        IsBusy = true;
        Status = L.T("서버를 확인하는 중…");
        try
        {
            await RefreshAsync(checkServer: true);
        }
        finally
        {
            IsBusy = false;
            Status = string.Empty;
        }
    }

    /// <summary>중단된 작업으로 잠긴 작업 사본 풀기 (svn cleanup).</summary>
    [RelayCommand]
    private async Task CleanupAsync()
    {
        if (IsBusy) return;
        if (await RunAsync(L.T("작업 사본을 정리하는 중…"), svn => svn.CleanupAsync())) _host.ShowNotice(L.T("작업 사본을 정리했습니다."));
    }

    partial void OnIsBranchPopupOpenChanged(bool value)
    {
        if (value) _ = LoadBranchesAsync();
    }

    private async Task LoadBranchesAsync()
    {
        if (_svn is not { } svn) return;
        if (_layout is not { } layout)
        {
            BranchMessage = L.T("trunk / branches / tags 배치가 아니어서 브랜치를 바꿀 수 없습니다.");
            return;
        }

        if (Branches.Count == 0) BranchMessage = L.T("브랜치 목록을 읽는 중…");
        try
        {
            var branches = await svn.BranchesAsync(layout);
            if (!ReferenceEquals(svn, _svn)) return;
            if (!Branches.SequenceEqual(branches))
            {
                Branches.Clear();
                foreach (var branch in branches) Branches.Add(branch);
            }

            BranchMessage = null;
        }
        catch (SvnException ex)
        {
            BranchMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SwitchAsync(SvnBranch? branch)
    {
        IsBranchPopupOpen = false;
        if (branch is null || branch.IsCurrent || IsBusy || _layout is not { } layout) return;
        if (await RunAsync(L.F("'{0}' 브랜치로 전환하는 중…", branch.Name), svn => svn.SwitchAsync(branch, layout))) RefreshExplorer();
    }

    [RelayCommand]
    private async Task CreateBranchAsync()
    {
        var name = NewBranchName.Trim();
        if (name.Length == 0 || IsBusy || _layout is not { } layout) return;
        if (name.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '^' or '@'))
        {
            _host.ShowError(L.T("브랜치 이름에는 공백과 / \\ ^ @를 쓸 수 없습니다."));
            return;
        }

        IsBranchPopupOpen = false;
        if (!await RunAsync(L.F("'{0}' 브랜치를 만드는 중…", name), svn => svn.CreateBranchAsync(name, layout))) return;
        NewBranchName = string.Empty;
        RefreshExplorer();
        _host.ShowNotice(L.F("'{0}' 브랜치를 만들고 전환했습니다.", name));
    }

    private void RefreshExplorer()
    {
        if (WorkingCopyRoot is { } root) _host.RefreshFolder(root);
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
        if (_svn is not { } svn) return;
        _historyStale = false;
        try
        {
            long? before = reset || History.Count == 0 ? null : History[^1].Revision.Revision;
            var revisions = await svn.LogAsync(before, HistoryPageSize);
            if (!ReferenceEquals(svn, _svn)) return;

            var items = revisions.Select(r => new RevisionItem(r)).ToList();
            if (reset)
            {
                // 첫 쪽이 그대로면 목록(과 더 불러온 쪽)을 유지한다
                var same = History.Count >= items.Count
                           && items.Select((item, i) => (item, i)).All(p => History[p.i].Revision.Revision == p.item.Revision.Revision)
                           && (items.Count == HistoryPageSize || History.Count == items.Count);
                if (same) return;

                var selected = SelectedRevision?.Revision.Revision;
                History.Clear();
                foreach (var item in items) History.Add(item);
                CanLoadMore = items.Count == HistoryPageSize;
                SelectedRevision = History.FirstOrDefault(r => r.Revision.Revision == selected) ?? History.FirstOrDefault();
            }
            else
            {
                foreach (var item in items) History.Add(item);
                CanLoadMore = items.Count == HistoryPageSize;
            }
        }
        catch (SvnException ex)
        {
            if (ex.SvnMissing) Problem = ex.Message;
            else HistoryDiff = [new DiffLine(DiffLineKind.Note, ex.Message)];
        }
    }

    partial void OnSelectedRevisionChanged(RevisionItem? value) => _ = LoadRevisionFilesAsync(value);

    private async Task LoadRevisionFilesAsync(RevisionItem? revision)
    {
        RevisionFiles.Clear();
        HistoryDiff = [];
        if (revision is null || _svn is not { } svn) return;
        try
        {
            var paths = await svn.RevisionPathsAsync(revision.Revision.Revision);
            if (!ReferenceEquals(revision, SelectedRevision)) return;
            var checkout = _info is { } info ? (info.RelativeUrl.StartsWith('^') ? info.RelativeUrl[1..] : info.RelativeUrl) : string.Empty;
            foreach (var path in paths) RevisionFiles.Add(new RevisionFileItem(path, SvnParsers.DisplayPath(path.RepositoryPath, checkout)));
            SelectedRevisionFile = RevisionFiles.FirstOrDefault(f => !f.Path.IsDirectory) ?? RevisionFiles.FirstOrDefault();
        }
        catch (SvnException ex)
        {
            HistoryDiff = [new DiffLine(DiffLineKind.Note, ex.Message)];
        }
    }

    partial void OnSelectedRevisionFileChanged(RevisionFileItem? value) => _ = LoadHistoryDiffAsync(value);

    private async Task LoadHistoryDiffAsync(RevisionFileItem? file)
    {
        _historyDiffCts?.Cancel();
        var cts = _historyDiffCts = new CancellationTokenSource();
        if (file is null || SelectedRevision is not { } revision || _svn is not { } svn)
        {
            HistoryDiff = [];
            return;
        }

        try
        {
            var lines = await svn.RevisionDiffAsync(revision.Revision.Revision, file.Path, cts.Token);
            if (!cts.IsCancellationRequested && ReferenceEquals(file, SelectedRevisionFile)) HistoryDiff = lines;
        }
        catch (OperationCanceledException)
        {
            // 다른 파일을 골랐다
        }
        catch (SvnException ex)
        {
            HistoryDiff = [new DiffLine(DiffLineKind.Note, ex.Message)];
        }
    }

    // ── 설정 · 감시 ─────────────────────────────────────────────────

    partial void OnAutoOpenChanged(bool value) => _host.SaveData(new SvnSettings { AutoOpen = value });

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
        if (WorkingCopyRoot is { } root && IsRelevant(root, e.FullPath)) QueueRefresh();
    }

    private void QueueRefresh()
    {
        // 빌드처럼 이벤트가 몰릴 때 UI 스레드로 한 번만 넘긴다
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0) _dispatcher.BeginInvoke(new Action(ScheduleRefresh));
    }

    /// <summary>
    /// .svn 폴더 안은 보지 않는다 (svn 자신이 상태를 읽을 때도 건드리므로 감시하면 새로 고침이 끝없이 돈다).
    /// 다른 프로그램(TortoiseSVN 등)의 svn 작업은 창을 다시 활성화할 때 반영된다.
    /// </summary>
    internal static bool IsRelevant(string root, string fullPath)
    {
        if (fullPath.Length <= root.Length) return false;
        var relative = fullPath[root.Length..].TrimStart('\\', '/');
        return !relative.Equals(".svn", StringComparison.OrdinalIgnoreCase)
               && !relative.StartsWith(".svn\\", StringComparison.OrdinalIgnoreCase)
               && !relative.StartsWith(".svn/", StringComparison.OrdinalIgnoreCase);
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
        return time.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static bool SameRoot(string? a, string? b)
        => a is not null && b is not null
           && string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

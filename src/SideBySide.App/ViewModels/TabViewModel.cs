using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SideBySide.App.Infrastructure;
using SideBySide.App.Services;
using SideBySide.Core.FileSystem;

namespace SideBySide.App.ViewModels;

public enum NavKind
{
    Open,
    Back,
    Forward,
    Up,
}

public enum SortColumn
{
    Name,
    Modified,
    Type,
    Size,
}

/// <summary>
/// 뷰에 "이 항목들을 선택해 달라"고 요청. <see cref="Indices"/>가 있으면 위치로, 없으면 이름으로 찾는다.
/// <see cref="BeginRename"/>이면 첫 항목 이름 편집 시작.
/// </summary>
public sealed record SelectRequest(IReadOnlyCollection<string> Names, bool BeginRename = false, IReadOnlyCollection<int>? Indices = null);

/// <summary>
/// 탭 하나 = 폴더 하나의 목록 + 방문 기록 + 정렬/검색 상태.
/// 디렉터리 열거·필터·정렬은 백그라운드에서 하고 결과만 UI 스레드에서 한 번에 교체한다.
/// </summary>
public sealed partial class TabViewModel : ObservableObject, IDisposable
{
    public const string ThisPc = "";

    private readonly ExplorerContext _ctx;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private readonly List<string> _back = [];
    private readonly List<string> _forward = [];

    private List<FileEntry> _raw = [];
    private List<FileItemViewModel>? _drives;
    private CancellationTokenSource? _loadCts;
    private FileSystemWatcher? _watcher;
    private IReadOnlyList<FileItemViewModel> _selected = [];
    private int _buildVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(IsThisPc), nameof(CanGoUp))]
    private string _path = ThisPc;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private SortColumn _sortBy = SortColumn.Name;

    [ObservableProperty]
    private bool _sortDescending;

    [ObservableProperty]
    private int _hiddenCount;

    /// <summary>창의 활성 탭인지 (탭 표시줄 강조용).</summary>
    [ObservableProperty]
    private bool _isActive;

    public TabViewModel(ExplorerContext ctx, string path)
    {
        _ctx = ctx;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, OnRefreshTimer, _dispatcher);
        _refreshTimer.Stop();
        _path = NormalizePath(path);
        _ctx.FilterChanged += OnFilterChanged;
        _ctx.CutPathsChanged += OnCutPathsChanged;
    }

    public BulkObservableCollection<FileItemViewModel> Items { get; } = [];

    public string Title => IsThisPc ? "내 PC"
        : System.IO.Path.GetPathRoot(Path) == Path ? Path.TrimEnd('\\')
        : System.IO.Path.GetFileName(Path);

    public bool IsThisPc => Path.Length == 0;

    /// <summary>한 번이라도 목록을 읽었는지 (비활성 탭은 처음 활성화될 때 읽는다).</summary>
    public bool HasLoaded { get; private set; }

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public bool CanGoUp => !IsThisPc;

    public IReadOnlyList<FileItemViewModel> SelectedItems => _selected;

    public FileItemViewModel? SelectedItem => _selected.Count > 0 ? _selected[0] : null;

    /// <summary>선택 항목 중 실제 파일/폴더 경로 (빈 행·드라이브 제외).</summary>
    public IReadOnlyList<string> SelectedPaths => _selected.Where(i => !i.IsPlaceholder && !i.IsDrive).Select(i => i.FullPath).ToList();

    /// <summary>비교 모드에서는 비교 엔진이 탐색을 가로채 양쪽을 함께 이동시킨다.</summary>
    public Func<TabViewModel, NavKind, string, Task>? NavigationInterceptor { get; set; }

    public bool IsCompareView => NavigationInterceptor is not null;

    public event EventHandler? SelectionChanged;

    public event EventHandler<SelectRequest>? SelectRequested;

    /// <summary>비교 모드에서 폴더 내용이 바뀌었음 (비교 엔진이 다시 비교).</summary>
    public event EventHandler? ExternalChange;

    public static string NormalizePath(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0 || path == "내 PC") return ThisPc;
        path = Environment.ExpandEnvironmentVariables(path);
        if (path.Length == 2 && path[1] == ':') path += "\\";
        try
        {
            path = System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }

        var root = System.IO.Path.GetPathRoot(path);
        return path.Length > (root?.Length ?? 0) ? System.IO.Path.TrimEndingDirectorySeparator(path) : path;
    }

    public static string ParentOf(string path)
        => path.Length == 0 ? ThisPc : System.IO.Path.GetDirectoryName(path) ?? ThisPc;

    public void SetSelection(IReadOnlyList<FileItemViewModel> items)
    {
        // ListView는 클릭한 순서로 돌려주므로 화면 순서로 정렬 (다중 이름 바꾸기 번호, 복사 순서 등)
        if (items.Count > 1)
        {
            var position = new Dictionary<FileItemViewModel, int>(Items.Count, ReferenceEqualityComparer.Instance);
            for (int i = 0; i < Items.Count; i++) position[Items[i]] = i;
            items = items.OrderBy(i => position.GetValueOrDefault(i, int.MaxValue)).ToList();
        }

        _selected = items;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RequestSelect(IReadOnlyCollection<string> names, bool beginRename = false)
        => SelectRequested?.Invoke(this, new SelectRequest(names, beginRename));

    public void RequestSelectIndices(IReadOnlyCollection<int> indices)
        => SelectRequested?.Invoke(this, new SelectRequest([], Indices: indices));

    // ── 탐색 ─────────────────────────────────────────────────────────

    public async Task NavigateAsync(string path, string? selectName = null)
    {
        path = NormalizePath(path);
        if (path.Length > 0 && File.Exists(path))
        {
            selectName = System.IO.Path.GetFileName(path);
            path = ParentOf(path);
        }

        if (NavigationInterceptor is not null)
        {
            await NavigationInterceptor(this, NavKind.Open, path);
            return;
        }

        if (ApplyNavigation(NavKind.Open, path) is null) return;
        await LoadAsync(selectName is null ? null : [selectName]);
    }

    public Task GoBackAsync() => GoAsync(NavKind.Back);

    public Task GoForwardAsync() => GoAsync(NavKind.Forward);

    public Task GoUpAsync() => GoAsync(NavKind.Up);

    /// <summary>
    /// 기록/경로만 갱신하고 목록은 읽지 않는다 (비교 엔진이 양쪽에 호출).
    /// 반환: 이동한 경우 이전 경로, 이동할 수 없으면 null.
    /// </summary>
    public string? ApplyNavigation(NavKind kind, string? target = null)
    {
        var previous = Path;
        switch (kind)
        {
            case NavKind.Open:
                if (target is null || string.Equals(target, Path, StringComparison.OrdinalIgnoreCase)) return target is null ? null : previous;
                _back.Add(Path);
                _forward.Clear();
                Path = target;
                break;
            case NavKind.Up:
                if (IsThisPc) return null;
                _back.Add(Path);
                _forward.Clear();
                Path = ParentOf(Path);
                break;
            case NavKind.Back:
                if (_back.Count == 0) return null;
                _forward.Add(Path);
                Path = _back[^1];
                _back.RemoveAt(_back.Count - 1);
                break;
            case NavKind.Forward:
                if (_forward.Count == 0) return null;
                _back.Add(Path);
                Path = _forward[^1];
                _forward.RemoveAt(_forward.Count - 1);
                break;
        }

        SearchText = string.Empty;
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        SetupWatcher();
        return previous;
    }

    private async Task GoAsync(NavKind kind)
    {
        if (NavigationInterceptor is not null)
        {
            await NavigationInterceptor(this, kind, Path);
            return;
        }

        var previous = ApplyNavigation(kind);
        if (previous is null) return;

        // 상위/뒤로 이동 시 방금 있던 폴더를 선택 (탐색기 동작)
        var child = ParentOf(previous).Equals(Path, StringComparison.OrdinalIgnoreCase) ? System.IO.Path.GetFileName(previous) : null;
        if (child is { Length: 0 }) child = previous; // 드라이브 루트 → 내 PC
        await LoadAsync(child is null ? null : [child]);
    }

    // ── 로드 / 필터 / 정렬 ───────────────────────────────────────────

    public Task RefreshAsync()
    {
        if (IsCompareView)
        {
            ExternalChange?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        return LoadAsync(_selected.Select(s => s.Name).ToList());
    }

    public async Task LoadAsync(IReadOnlyCollection<string>? select = null)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        HasLoaded = true;
        IsLoading = true;
        Error = null;
        var path = Path;

        try
        {
            if (path.Length == 0)
            {
                _drives = await Task.Run(LoadDrives, cts.Token);
                _raw = [];
            }
            else
            {
                _drives = null;
                _raw = await DirectoryLoader.LoadAsync(path, cts.Token);
            }

            if (cts.IsCancellationRequested) return;
            if (_watcher is null) SetupWatcher();
            await RebuildAsync(select);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            if (cts.IsCancellationRequested) return;
            _raw = [];
            Items.ReplaceAll([]);
            Error = ex switch
            {
                UnauthorizedAccessException => $"'{path}'에 접근할 수 없습니다. 권한이 없습니다.",
                DirectoryNotFoundException => $"'{path}'을(를) 찾을 수 없습니다.",
                _ => ex.Message,
            };
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) IsLoading = false;
        }
    }

    /// <summary>비교 모드: 비교 엔진이 만든 정렬된 행을 그대로 표시.</summary>
    public void ShowRows(IReadOnlyList<FileItemViewModel> rows, IReadOnlyCollection<string>? select = null)
    {
        _loadCts?.Cancel();
        HasLoaded = true;
        IsLoading = false;
        Error = null;
        HiddenCount = 0;
        foreach (var vm in rows) vm.IsCut = _ctx.CutPaths.Contains(vm.FullPath);
        Items.ReplaceAll(rows);
        if (select is { Count: > 0 }) RequestSelect(select);
    }

    public void Sort(SortColumn column)
    {
        if (IsCompareView) return;
        if (SortBy == column) SortDescending = !SortDescending;
        else
        {
            SortBy = column;
            SortDescending = column is SortColumn.Modified or SortColumn.Size;
        }

        _ = RebuildAsync(_selected.Select(s => s.Name).ToList());
    }

    partial void OnSearchTextChanged(string value)
    {
        if (!IsCompareView) _ = RebuildAsync(null);
    }

    private async Task RebuildAsync(IReadOnlyCollection<string>? select)
    {
        var version = ++_buildVersion;
        if (_drives is not null)
        {
            Items.ReplaceAll(_drives);
            HiddenCount = 0;
            if (select is { Count: > 0 }) RequestSelect(select);
            return;
        }

        var raw = _raw;
        var filter = _ctx.Filter;
        var search = SearchText.Trim();
        var sortBy = SortBy;
        var descending = SortDescending;
        var cache = Items.Where(i => i.Entry is not null).ToDictionary(i => i.FullPath, StringComparer.OrdinalIgnoreCase);

        var (items, hidden) = await Task.Run(() =>
        {
            var matcher = BuildSearch(search);
            var visible = raw.Where(e => filter.IsVisible(e)).ToList();
            var hiddenCount = raw.Count - visible.Count;
            var vms = visible
                .Where(e => matcher is null || matcher(e.Name))
                .Select(e => cache.TryGetValue(e.FullPath, out var old) && old.Entry == e ? old : new FileItemViewModel(e, _ctx.Icons))
                .ToList();
            return (Sort(vms, sortBy, descending), hiddenCount);
        });

        if (version != _buildVersion) return;
        foreach (var vm in items) vm.IsCut = _ctx.CutPaths.Contains(vm.FullPath);
        HiddenCount = hidden;
        Items.ReplaceAll(items);
        if (select is { Count: > 0 }) RequestSelect(select);
    }

    private static Func<string, bool>? BuildSearch(string search)
    {
        if (search.Length == 0) return null;
        if (search.Contains('*') || search.Contains('?'))
        {
            var filter = Core.Filtering.FilterSet.Create(new Core.Filtering.FilterSettings { Include = search }, showHidden: true);
            return name => filter.IsVisible(name, false, FileAttributes.Normal);
        }

        return name => name.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static List<FileItemViewModel> Sort(List<FileItemViewModel> items, SortColumn by, bool descending)
    {
        Comparison<FileItemViewModel> byName = (a, b) => NaturalStringComparer.Instance.Compare(a.Name, b.Name);
        Comparison<FileItemViewModel> key = by switch
        {
            SortColumn.Modified => (a, b) => a.Modified.CompareTo(b.Modified),
            SortColumn.Size => (a, b) => a.Size.CompareTo(b.Size),
            SortColumn.Type => (a, b) => string.Compare(a.TypeText, b.TypeText, StringComparison.CurrentCultureIgnoreCase),
            _ => byName,
        };

        items.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            var c = key(a, b);
            if (c == 0 && by != SortColumn.Name) c = byName(a, b);
            return descending ? -c : c;
        });
        return items;
    }

    private List<FileItemViewModel> LoadDrives()
    {
        var list = new List<FileItemViewModel>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            double usage = 0;
            var space = string.Empty;
            try
            {
                if (drive.IsReady && drive.TotalSize > 0)
                {
                    usage = 1 - (double)drive.AvailableFreeSpace / drive.TotalSize;
                    space = $"{Formatting.Size(drive.AvailableFreeSpace)} 남음 / {Formatting.Size(drive.TotalSize)}";
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            var entry = new FileEntry(drive.Name, drive.Name, true, 0, DateTime.MinValue, FileAttributes.Directory);
            list.Add(new FileItemViewModel(entry, _ctx.Icons)
            {
                IsDrive = true,
                DisplayName = _ctx.Icons.GetDisplayName(drive.Name),
                DriveUsage = usage,
                DriveSpaceText = space,
            });
        }

        return list;
    }

    // ── 변경 감시 ───────────────────────────────────────────────────

    private void SetupWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (IsThisPc || !Directory.Exists(Path)) return;

        try
        {
            var watcher = new FileSystemWatcher(Path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
                               | NotifyFilters.LastWrite | NotifyFilters.Attributes,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += OnFileSystemEvent;
            watcher.Deleted += OnFileSystemEvent;
            watcher.Changed += OnFileSystemEvent;
            watcher.Renamed += OnFileSystemEvent;
            watcher.Error += (_, _) => _dispatcher.BeginInvoke(RestartRefreshTimer);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException)
        {
            // 일부 네트워크/가상 드라이브는 감시를 지원하지 않음 → F5로 수동 새로 고침
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) => _dispatcher.BeginInvoke(RestartRefreshTimer);

    private void RestartRefreshTimer()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void OnRefreshTimer(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();

        // 이름 편집 중에 목록을 다시 만들면 입력란이 사라지므로 편집이 끝날 때까지 미룬다
        if (Items.Any(i => i.IsEditing))
        {
            _refreshTimer.Start();
            return;
        }

        _ = RefreshAsync();
    }

    private void OnFilterChanged(object? sender, EventArgs e)
    {
        if (!IsCompareView) _ = RebuildAsync(_selected.Select(s => s.Name).ToList());
    }

    private void OnCutPathsChanged(object? sender, EventArgs e)
    {
        foreach (var vm in Items) vm.IsCut = _ctx.CutPaths.Contains(vm.FullPath);
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        _watcher?.Dispose();
        _refreshTimer.Stop();
        _ctx.FilterChanged -= OnFilterChanged;
        _ctx.CutPathsChanged -= OnCutPathsChanged;
    }
}

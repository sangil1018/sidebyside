using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SideBySide.App.Services;
using SideBySide.Core.Compare;

namespace SideBySide.App.ViewModels;

/// <summary>
/// 폴더 비교 모드. 왼쪽/오른쪽 활성 탭을 묶어 같은 이름끼리 같은 행에 정렬해 보여주고,
/// 한쪽에서 폴더를 이동하면 반대쪽도 같은 상대 경로로 이동한다.
/// </summary>
public sealed partial class CompareViewModel : ObservableObject
{
    private readonly ExplorerContext _ctx;
    private readonly FolderComparer _comparer = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _republishTimer;
    private readonly DispatcherTimer _rerunTimer;
    private readonly Dictionary<CompareRow, (FileItemViewModel Left, FileItemViewModel Right)> _vmByRow = [];

    private TabViewModel? _left;
    private TabViewModel? _right;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<CompareRow> _rows = [];
    private bool _syncingSelection;
    private bool _comparing;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _showDifferencesOnly;

    [ObservableProperty]
    private CompareMethod _method;

    [ObservableProperty]
    private double _timeToleranceSeconds;

    [ObservableProperty]
    private bool _ignoreHourOffset;

    [ObservableProperty]
    private bool _compareSubfolders;

    public CompareViewModel(ExplorerContext ctx)
    {
        _ctx = ctx;
        var o = ctx.Settings.Compare;
        _method = o.Method;
        _timeToleranceSeconds = o.TimeToleranceSeconds;
        _ignoreHourOffset = o.IgnoreHourOffset;
        _compareSubfolders = o.CompareSubfolders;
        _showDifferencesOnly = ctx.Settings.ShowDifferencesOnly;

        _republishTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => { _republishTimer!.Stop(); PublishKeepingSelection(); }, _dispatcher);
        _republishTimer.Stop();
        _rerunTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => { _rerunTimer!.Stop(); _ = RunAsync(clearCache: true, keepSelection: true); }, _dispatcher);
        _rerunTimer.Stop();
        _ctx.FilterChanged += (_, _) =>
        {
            if (IsActive) _ = RunAsync(clearCache: true, keepSelection: true);
        };
    }

    public CompareOptions Options => new()
    {
        Method = Method,
        TimeToleranceSeconds = TimeToleranceSeconds,
        IgnoreHourOffset = IgnoreHourOffset,
        CompareSubfolders = CompareSubfolders,
    };

    public static IReadOnlyList<CompareMethod> Methods { get; } = [CompareMethod.SizeAndDate, CompareMethod.Content];

    /// <summary>양쪽에 있는 파일을 열려고 할 때 (파일 비교 화면 열기).</summary>
    public event EventHandler<(string Left, string Right)>? FileDiffRequested;

    public void Enter(TabViewModel left, TabViewModel right)
    {
        _left = left;
        _right = right;
        foreach (var tab in new[] { left, right })
        {
            tab.NavigationInterceptor = InterceptAsync;
            tab.ExternalChange += OnExternalChange;
            tab.SelectionChanged += OnSelectionChanged;
        }

        IsActive = true;
        _ = RunAsync(clearCache: true);
    }

    public void Exit()
    {
        _cts?.Cancel();
        _republishTimer.Stop();
        _rerunTimer.Stop();
        foreach (var tab in new[] { _left, _right })
        {
            if (tab is null) continue;
            tab.NavigationInterceptor = null;
            tab.ExternalChange -= OnExternalChange;
            tab.SelectionChanged -= OnSelectionChanged;
            _ = tab.LoadAsync(tab.SelectedItems.Where(i => !i.IsPlaceholder).Select(i => i.Name).ToList());
        }

        _left = _right = null;
        _comparing = false;
        _rows = [];
        _vmByRow.Clear();
        IsActive = false;
        IsRunning = false;
        Summary = string.Empty;
    }

    public void SaveOptions()
    {
        _ctx.Settings.Compare = Options;
        _ctx.Settings.ShowDifferencesOnly = ShowDifferencesOnly;
    }

    public async Task RunAsync(bool clearCache = false, bool keepSelection = false, IReadOnlyCollection<string>? select = null)
    {
        if (_left is null || _right is null) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        if (clearCache) _comparer.ClearCache();
        if (keepSelection) select ??= _left.SelectedItems.Select(i => i.Name).ToList();

        var leftPath = _left.Path;
        var rightPath = _right.Path;
        if (leftPath.Length == 0 || rightPath.Length == 0)
        {
            // 한쪽이 "내 PC"면 비교할 수 없으므로 각 창을 일반 목록으로 보여주고 폴더를 고르게 한다.
            // 양쪽 모두 폴더가 되면(탐색이 가로채져 RunAsync가 다시 호출됨) 자동으로 비교한다.
            cts.Cancel();
            _comparing = false;
            _rows = [];
            _vmByRow.Clear();
            IsRunning = false;
            Summary = "양쪽 창에서 폴더를 열면 비교합니다.";
            await Task.WhenAll(_left.LoadAsync(select), _right.LoadAsync(select));
            return;
        }

        IsRunning = true;
        Summary = "비교 중…";
        var filter = _ctx.Filter;
        var options = Options;
        try
        {
            var rows = await _comparer.CompareLevelAsync(leftPath, rightPath, filter, options, cts.Token);
            if (cts.IsCancellationRequested) return;
            _rows = rows;
            Publish(select);
            UpdateSummary();

            await _comparer.ResolveDirectoriesAsync(rows, filter, options,
                row => _dispatcher.BeginInvoke(() => OnRowResolved(row, cts)), cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Summary = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsRunning = false;
                UpdateSummary();
            }
        }
    }

    public void Cancel()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        IsRunning = false;
        UpdateSummary();
    }

    /// <summary>비교 행 열기: 폴더는 양쪽 함께 이동, 양쪽에 있는 파일은 파일 비교.</summary>
    public bool TryOpen(FileItemViewModel item)
    {
        if (!IsActive || item.Row is not { } row || _left is null || _right is null) return false;

        if (row.IsDirectory)
        {
            var leftTarget = Path.Combine(_left.Path, row.Name);
            var rightTarget = Path.Combine(_right.Path, row.Name);
            _left.ApplyNavigation(NavKind.Open, leftTarget);
            _right.ApplyNavigation(NavKind.Open, rightTarget);
            _ = RunAsync();
            return true;
        }

        if (row.Left is not null && row.Right is not null)
        {
            FileDiffRequested?.Invoke(this, (row.Left.FullPath, row.Right.FullPath));
            return true;
        }

        return false;
    }

    /// <summary>현재 위치 다음(이전) 차이 행 인덱스. 없으면 -1.</summary>
    public int FindDifference(TabViewModel tab, int fromIndex, bool forward)
    {
        var items = tab.Items;
        for (int i = forward ? fromIndex + 1 : fromIndex - 1; i >= 0 && i < items.Count; i += forward ? 1 : -1)
        {
            if (items[i].CompareState is not (null or CompareState.Same)) return i;
        }

        return -1;
    }

    partial void OnShowDifferencesOnlyChanged(bool value)
    {
        SaveOptions();
        if (IsActive) PublishKeepingSelection();
    }

    partial void OnMethodChanged(CompareMethod value) => OptionsChanged();

    partial void OnTimeToleranceSecondsChanged(double value) => OptionsChanged();

    partial void OnIgnoreHourOffsetChanged(bool value) => OptionsChanged();

    partial void OnCompareSubfoldersChanged(bool value) => OptionsChanged();

    private void OptionsChanged()
    {
        SaveOptions();
        if (IsActive) _ = RunAsync(clearCache: true, keepSelection: true);
    }

    private async Task InterceptAsync(TabViewModel source, NavKind kind, string path)
    {
        if (_left is null || _right is null) return;
        var other = ReferenceEquals(source, _left) ? _right : _left;
        string? select = null;

        switch (kind)
        {
            case NavKind.Open:
                var relative = RelativeChild(source.Path, path);
                source.ApplyNavigation(NavKind.Open, path);
                if (relative is not null && other.Path.Length > 0)
                    other.ApplyNavigation(NavKind.Open, Path.Combine(other.Path, relative));
                break;
            case NavKind.Up:
                select = Path.GetFileName(source.Path);
                source.ApplyNavigation(NavKind.Up);
                if (other.Path.Length > 0 && Path.GetPathRoot(other.Path) != other.Path) other.ApplyNavigation(NavKind.Up);
                break;
            default:
                source.ApplyNavigation(kind);
                other.ApplyNavigation(kind);
                break;
        }

        await RunAsync(select: select is { Length: > 0 } ? [select] : null);
    }

    private static string? RelativeChild(string parent, string path)
    {
        if (parent.Length == 0) return null;
        var relative = Path.GetRelativePath(parent, path);
        return relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : relative;
    }

    private void Publish(IReadOnlyCollection<string>? select)
    {
        if (_left is null || _right is null) return;
        _comparing = true;
        _vmByRow.Clear();
        var leftItems = new List<FileItemViewModel>(_rows.Count);
        var rightItems = new List<FileItemViewModel>(_rows.Count);
        var icons = _ctx.Icons;

        foreach (var row in _rows)
        {
            if (ShowDifferencesOnly && row.State == CompareState.Same) continue;
            var l = row.Left is not null ? new FileItemViewModel(row.Left, icons) : FileItemViewModel.Placeholder(row.Name, row.IsDirectory, icons);
            var r = row.Right is not null ? new FileItemViewModel(row.Right, icons) : FileItemViewModel.Placeholder(row.Name, row.IsDirectory, icons);
            l.Partner = r;
            r.Partner = l;
            l.Row = r.Row = row;
            ApplyState(row, l, r);
            _vmByRow[row] = (l, r);
            leftItems.Add(l);
            rightItems.Add(r);
        }

        _syncingSelection = true;
        try
        {
            _left.ShowRows(leftItems, select);
            _right.ShowRows(rightItems, select);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void PublishKeepingSelection()
        => Publish(_left?.SelectedItems.Select(i => i.Name).ToList());

    private static void ApplyState(CompareRow row, FileItemViewModel left, FileItemViewModel right)
    {
        left.CompareState = right.CompareState = row.State;
        left.IsNewer = row.Newer == NewerSide.Left;
        right.IsNewer = row.Newer == NewerSide.Right;
    }

    private void OnRowResolved(CompareRow row, CancellationTokenSource cts)
    {
        if (cts.IsCancellationRequested || !_vmByRow.TryGetValue(row, out var vms)) return;
        ApplyState(row, vms.Left, vms.Right);
        if (ShowDifferencesOnly && row.State == CompareState.Same)
        {
            _republishTimer.Stop();
            _republishTimer.Start();
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (_rows.Count == 0)
        {
            if (!IsRunning && Summary == "비교 중…") Summary = "항목 없음";
            return;
        }

        int diff = 0, leftOnly = 0, rightOnly = 0, same = 0, pending = 0;
        foreach (var r in _rows)
        {
            switch (r.State)
            {
                case CompareState.Same: same++; break;
                case CompareState.LeftOnly: leftOnly++; break;
                case CompareState.RightOnly: rightOnly++; break;
                case CompareState.Pending: pending++; break;
                default: diff++; break;
            }
        }

        var text = diff + leftOnly + rightOnly == 0 && pending == 0
            ? $"모두 같음 ({same}개)"
            : $"다름 {diff} · 왼쪽만 {leftOnly} · 오른쪽만 {rightOnly} · 같음 {same}";
        if (pending > 0) text += $" · 폴더 확인 중 {pending}";
        Summary = text;
    }

    private void OnExternalChange(object? sender, EventArgs e)
    {
        _rerunTimer.Stop();
        _rerunTimer.Start();
    }

    /// <summary>한쪽 선택을 반대쪽 같은 행에 맞춘다.</summary>
    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_syncingSelection || !_comparing || sender is not TabViewModel source || _left is null || _right is null) return;
        var other = ReferenceEquals(source, _left) ? _right : _left;
        List<int> indices;
        if (source.SelectedItems.Count > 32)
        {
            var positions = new Dictionary<FileItemViewModel, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < source.Items.Count; i++) positions[source.Items[i]] = i;
            indices = source.SelectedItems.Select(i => positions.GetValueOrDefault(i, -1)).Where(i => i >= 0).ToList();
        }
        else
        {
            indices = source.SelectedItems.Select(i => source.Items.IndexOf(i)).Where(i => i >= 0).ToList();
        }

        _syncingSelection = true;
        try { other.RequestSelectIndices(indices); }
        finally { _syncingSelection = false; }
    }
}

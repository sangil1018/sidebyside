using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.TempCleaner;

/// <summary>카테고리 한 줄.</summary>
public sealed partial class CategoryViewModel(CleanTarget target) : ObservableObject
{
    public CleanTarget Target { get; } = target;

    public string Name => Target.Name;

    public string Group => Target.Group;

    public bool RequiresAdmin => Target.RequiresAdmin;

    [ObservableProperty]
    private bool _isChecked = target.DefaultChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private long _bytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private int _count;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private bool _scanned;

    public string SizeText => Scanned ? L.F("{0} · {1:N0}개", Cleaner.FormatBytes(Bytes), Count) : "…";
}

public sealed class CleanerSettings
{
    public int MinAgeDays { get; set; }

    public List<string> Unchecked { get; set; } = [];

    public List<string> Checked { get; set; } = [];
}

public sealed partial class CleanerViewModel : ObservableObject
{
    private const int MaxListed = 10_000;
    private readonly IModuleHost _host;
    private CancellationTokenSource? _cts;

    public CleanerViewModel(IModuleHost host)
    {
        _host = host;
        var s = host.LoadData<CleanerSettings>();
        _minAgeDays = Math.Clamp(s.MinAgeDays, 0, 3650);
        foreach (var target in Cleaner.BuildTargets())
        {
            var vm = new CategoryViewModel(target);
            if (s.Unchecked.Contains(target.Id)) vm.IsChecked = false;
            if (s.Checked.Contains(target.Id)) vm.IsChecked = true;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CategoryViewModel.IsChecked)) OnPropertyChanged(nameof(SelectedSummary));
            };
            Categories.Add(vm);
        }
    }

    public ObservableCollection<CategoryViewModel> Categories { get; } = [];

    public ObservableCollection<CleanFile> Files { get; } = [];

    [ObservableProperty]
    private int _minAgeDays;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFileList))]
    private CategoryViewModel? _listedCategory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CleanCommand), nameof(ShowFilesCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress = double.NaN;

    [ObservableProperty]
    private string _status = string.Empty;

    public bool HasFileList => ListedCategory is not null;

    public string SelectedSummary
    {
        get
        {
            var selected = Categories.Where(c => c.IsChecked && c.Scanned).ToList();
            return L.F("선택: {0}", Cleaner.FormatBytes(selected.Sum(c => c.Bytes)));
        }
    }

    partial void OnMinAgeDaysChanged(int value)
    {
        if (value is < 0 or > 3650) MinAgeDays = Math.Clamp(value, 0, 3650);
    }

    private bool NotBusy() => !IsBusy;

    /// <summary>모든 카테고리를 동시에 검사한다 (카테고리마다 다른 폴더라 병렬이 빠르다).</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        Progress = double.NaN;
        Status = L.T("검사 중…");
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var days = MinAgeDays;
        try
        {
            foreach (var c in Categories) c.Scanned = false;
            await Task.WhenAll(Categories.Select(async c =>
            {
                var (count, bytes) = await Task.Run(() => Cleaner.Scan(c.Target, days, token), token);
                c.Count = count;
                c.Bytes = bytes;
                c.Scanned = true;
            }));
            Status = L.F("전체 {0} · 정리할 카테고리를 고르고 '정리'를 누르세요.", Cleaner.FormatBytes(Categories.Sum(c => c.Bytes)));
        }
        catch (OperationCanceledException)
        {
            Status = L.T("취소했습니다.");
        }
        finally
        {
            Finish();
            OnPropertyChanged(nameof(SelectedSummary));
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task CleanAsync()
    {
        var selected = Categories.Where(c => c.IsChecked).ToList();
        if (selected.Count == 0)
        {
            Status = L.T("정리할 카테고리를 고르세요.");
            return;
        }

        var admin = selected.Where(c => c.RequiresAdmin).ToList();
        var message = L.F("선택한 {0}개 카테고리의 파일을 영구 삭제합니다 (휴지통을 거치지 않음). 사용 중인 파일은 건너뜁니다.", selected.Count)
                      + (admin.Count > 0 ? "\n\n" + L.T("시스템 폴더 정리를 위해 관리자 권한 확인 창이 뜹니다.") : string.Empty);
        if (!_host.Confirm(message, L.T("임시 파일 정리"))) return;

        Save();
        IsBusy = true;
        Progress = 0;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var days = MinAgeDays;
        int deleted = 0, skipped = 0;
        long freed = 0;
        var done = 0;
        try
        {
            foreach (var c in selected.Where(c => !c.RequiresAdmin))
            {
                Status = L.F("정리 중: {0}", c.Name);
                var r = await Task.Run(() => Cleaner.Clean(c.Target, days, token), token);
                deleted += r.DeletedFiles;
                skipped += r.SkippedFiles;
                freed += r.FreedBytes;
                Progress = ++done / (double)selected.Count;
            }

            if (admin.Count > 0)
            {
                Status = L.T("관리자 권한으로 시스템 폴더 정리 중…");
                var payload = JsonSerializer.Serialize(new CleanRequest { Ids = admin.Select(c => c.Target.Id).ToList(), MinAgeDays = days });
                var result = await _host.RunElevatedAsync(CleanerElevated.Operation, payload, token);
                if (result.Cancelled)
                {
                    skipped += admin.Sum(c => c.Count);
                    _host.ShowNotice(L.T("관리자 권한을 거부해 시스템 폴더는 정리하지 않았습니다."));
                }
                else if (result.Succeeded && JsonSerializer.Deserialize<CleanResponse>(result.Output!) is { } response)
                {
                    foreach (var r in response.Results.Values)
                    {
                        deleted += r.DeletedFiles;
                        skipped += r.SkippedFiles;
                        freed += r.FreedBytes;
                    }
                }
                else
                {
                    _host.ShowError(result.Error ?? L.T("관리자 권한 작업이 실패했습니다."));
                }

                Progress = 1;
            }

            Status = L.F("완료: {0:N0}개 삭제 ({1} 확보), {2:N0}개 건너뜀 (사용 중·권한 없음)", deleted, Cleaner.FormatBytes(freed), skipped);
        }
        catch (OperationCanceledException)
        {
            Status = L.T("취소했습니다.");
        }
        catch (JsonException ex)
        {
            _host.ShowError(ex.Message);
        }
        finally
        {
            Finish();
        }

        await RescanAsync(selected);
    }

    private async Task RescanAsync(IReadOnlyList<CategoryViewModel> categories)
    {
        var days = MinAgeDays;
        foreach (var c in categories)
        {
            var (count, bytes) = await Task.Run(() => Cleaner.Scan(c.Target, days));
            c.Count = count;
            c.Bytes = bytes;
            c.Scanned = true;
        }

        OnPropertyChanged(nameof(SelectedSummary));
        if (ListedCategory is { } listed && categories.Contains(listed)) await ShowFilesAsync(listed);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ShowFilesAsync(CategoryViewModel? category)
    {
        if (category is null || category.Target.IsRecycleBin) return;
        ListedCategory = category;
        Files.Clear();
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var days = MinAgeDays;
        try
        {
            var (files, truncated) = await Task.Run(() => Cleaner.ListFiles(category.Target, days, MaxListed, token), token);
            foreach (var f in files) Files.Add(f);
            Status = truncated ? L.F("큰 파일부터 {0:N0}개만 표시합니다.", MaxListed) : L.F("{0:N0}개 파일", files.Count);
        }
        catch (OperationCanceledException)
        {
            Status = L.T("취소했습니다.");
        }
        finally
        {
            Finish();
        }
    }

    [RelayCommand]
    private void CloseFiles()
    {
        ListedCategory = null;
        Files.Clear();
    }

    /// <summary>파일 목록에서 고른 파일만 삭제.</summary>
    public async Task DeleteFilesAsync(IReadOnlyList<CleanFile> files)
    {
        if (ListedCategory is not { } category || files.Count == 0 || IsBusy) return;
        if (!_host.Confirm(L.F("파일 {0}개를 영구 삭제합니다. 되돌릴 수 없습니다.", files.Count), L.T("임시 파일 정리"))) return;

        IsBusy = true;
        try
        {
            var r = await Task.Run(() => Cleaner.DeleteFiles(category.Target, files.Select(f => f.Path).ToList()));
            Status = L.F("{0:N0}개 삭제 ({1} 확보), {2:N0}개 건너뜀", r.DeletedFiles, Cleaner.FormatBytes(r.FreedBytes), r.SkippedFiles);
        }
        finally
        {
            Finish();
        }

        await RescanAsync([category]);
    }

    [RelayCommand]
    private void OpenFolder(CategoryViewModel? category)
    {
        if (category?.Target.Paths.FirstOrDefault(Directory.Exists) is { } path) _host.NavigateTo(path);
    }

    [RelayCommand]
    private void CancelWork() => Cancel();

    public void Cancel() => _cts?.Cancel();

    private void Finish()
    {
        IsBusy = false;
        Progress = double.NaN;
        _cts?.Dispose();
        _cts = null;
    }

    public void Save() => _host.SaveData(new CleanerSettings
    {
        MinAgeDays = MinAgeDays,
        Unchecked = Categories.Where(c => !c.IsChecked && c.Target.DefaultChecked).Select(c => c.Target.Id).ToList(),
        Checked = Categories.Where(c => c.IsChecked && !c.Target.DefaultChecked).Select(c => c.Target.Id).ToList(),
    });
}

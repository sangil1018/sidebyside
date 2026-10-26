using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SideBySide.Core.Localization;
using SideBySide.Core.Sequences;

namespace SideBySide.Modules.SeqRenumber;

/// <summary>설정에 저장하는 마지막 옵션.</summary>
public sealed class RenumberSettings
{
    public bool Renumber { get; set; } = true;

    public int StartFrame { get; set; } = 1001;

    public int Padding { get; set; } = 4;

    public RenumberMode Mode { get; set; } = RenumberMode.Copy;
}

/// <summary>미리보기 한 줄.</summary>
public sealed record RenumberPreviewRow(string OldName, string NewName);

public sealed partial class RenumberViewModel : ObservableObject
{
    private readonly IModuleHost _host;
    private CancellationTokenSource? _cts;
    private bool _loading;

    public RenumberViewModel(IModuleHost host)
    {
        _host = host;
        var s = host.LoadData<RenumberSettings>();
        _renumber = s.Renumber;
        _startFrame = s.StartFrame;
        _padding = Math.Clamp(s.Padding, 1, 9);
        _mode = s.Mode;
    }

    public ObservableCollection<ImageSequence> Sequences { get; } = [];

    public ObservableCollection<RenumberPreviewRow> Preview { get; } = [];

    [ObservableProperty]
    private string? _folder;

    [ObservableProperty]
    private ImageSequence? _sequence;

    [ObservableProperty]
    private string _prefix = string.Empty;

    [ObservableProperty]
    private string _separator = string.Empty;

    [ObservableProperty]
    private bool _renumber;

    [ObservableProperty]
    private int _startFrame;

    [ObservableProperty]
    private int _padding;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCopyMode), nameof(IsRenameMode))]
    private RenumberMode _mode;

    [ObservableProperty]
    private string _problem = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _canRun;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress = double.NaN;

    [ObservableProperty]
    private string _status = string.Empty;

    public bool IsCopyMode
    {
        get => Mode == RenumberMode.Copy;
        set { if (value) Mode = RenumberMode.Copy; }
    }

    public bool IsRenameMode
    {
        get => Mode == RenumberMode.Rename;
        set { if (value) Mode = RenumberMode.Rename; }
    }

    public string Summary => Sequence is { } s
        ? L.F("{0} · {1}–{2} · {3}개 프레임", s.DisplayName, s.FirstFrame, s.LastFrame, s.Frames.Count)
        : L.T("시퀀스 파일이나 폴더를 우클릭 > 시퀀스 번호 다시 매기기, 또는 '폴더 열기'를 누르세요.");

    /// <summary>대상 설정: 폴더 하나 또는 그 폴더의 프레임 파일들.</summary>
    public async Task LoadAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy) return;
        string? folder = null;
        string? pickName = null;
        if (paths.Count > 0 && Directory.Exists(paths[0]))
        {
            folder = paths[0];
        }
        else if (paths.Count > 0 && File.Exists(paths[0]))
        {
            folder = Path.GetDirectoryName(paths[0]);
            pickName = Path.GetFileName(paths[0]);
        }

        if (folder is null) return;
        Folder = folder;
        var found = await Task.Run(() => SequenceDetector.Scan(folder, maxDepth: 0));

        Sequences.Clear();
        foreach (var s in found) Sequences.Add(s);

        // 우클릭한 파일이 속한 시퀀스를 먼저 고른다
        Sequence = pickName is not null && SequenceName.TryParse(pickName, out var name)
            ? found.FirstOrDefault(s => string.Equals(s.Prefix + s.Separator + "#." + s.Extension, name.Prefix + name.Separator + "#." + name.Extension, StringComparison.OrdinalIgnoreCase)) ?? found.FirstOrDefault()
            : found.FirstOrDefault();
        if (found.Count == 0) Problem = L.T("이 폴더에서 번호 붙은 시퀀스를 찾지 못했습니다.");
    }

    partial void OnSequenceChanged(ImageSequence? value)
    {
        _loading = true;
        Prefix = value?.Prefix ?? string.Empty;
        Separator = value?.Separator ?? string.Empty;
        if (value is not null && value.Padding > Padding) Padding = value.Padding;
        _loading = false;
        OnPropertyChanged(nameof(Summary));
        UpdatePlan();
    }

    partial void OnPrefixChanged(string value) => UpdatePlan();

    partial void OnSeparatorChanged(string value) => UpdatePlan();

    partial void OnRenumberChanged(bool value) => UpdatePlan();

    partial void OnStartFrameChanged(int value) => UpdatePlan();

    partial void OnPaddingChanged(int value) => UpdatePlan();

    partial void OnModeChanged(RenumberMode value) => UpdatePlan();

    private RenumberPlan? _plan;

    /// <summary>입력이 바뀔 때마다 계획을 다시 세워 미리보기와 오류를 갱신한다 (파일은 건드리지 않음).</summary>
    private void UpdatePlan()
    {
        if (_loading) return;
        Preview.Clear();
        _plan = null;
        CanRun = false;
        if (Sequence is not { } sequence)
        {
            Problem = string.Empty;
            return;
        }

        var target = Mode == RenumberMode.Copy ? SequenceRenumberer.CopyFolderFor(sequence.Directory) : sequence.Directory;
        IEnumerable<string> existing;
        try
        {
            existing = Directory.Exists(target) ? Directory.EnumerateFiles(target).Select(Path.GetFileName).OfType<string>().ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            existing = [];
        }

        var options = new RenumberOptions(
            string.Equals(Prefix, sequence.Prefix, StringComparison.Ordinal) ? null : Prefix,
            Renumber,
            StartFrame,
            Math.Clamp(Padding, 1, 9),
            string.Equals(Separator, sequence.Separator, StringComparison.Ordinal) ? null : Separator);
        _plan = SequenceRenumberer.Plan(sequence, options, Mode, existing);

        foreach (var item in _plan.Items.Take(2000)) Preview.Add(new RenumberPreviewRow(Path.GetFileName(item.SourcePath), item.NewName));
        Problem = _plan.Errors.Count > 0 ? string.Join("\n", _plan.Errors) : string.Empty;
        CanRun = _plan.CanRun && _plan.Items.Any(i => !string.Equals(Path.GetFileName(i.SourcePath), i.NewName, StringComparison.Ordinal) || Mode == RenumberMode.Copy);
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (_host.PickFolder(L.T("시퀀스가 있는 폴더"), Folder ?? _host.ActiveFolder) is { } folder) await LoadAsync([folder]);
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (Folder is { } folder) await LoadAsync([folder]);
    }

    private bool CanExecuteRun() => CanRun && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanExecuteRun))]
    private async Task RunAsync()
    {
        if (_plan is not { CanRun: true } plan || Sequence is not { } sequence) return;
        Save();
        if (Mode == RenumberMode.Rename
            && !_host.Confirm(L.F("원본 파일 {0}개의 이름을 바꿉니다. 계속할까요?", plan.Items.Count), L.T("시퀀스 번호 다시 매기기")))
            return;

        IsBusy = true;
        Progress = 0;
        _cts = new CancellationTokenSource();
        var total = plan.Items.Count;
        var progress = new Progress<int>(done =>
        {
            Progress = done / (double)total;
            Status = L.F("{0} / {1}", done, total);
        });
        var mode = Mode;
        try
        {
            var result = await Task.Run(() => SequenceRenumberer.Execute(plan, mode, progress, _cts.Token));
            if (result.RolledBack)
            {
                Status = L.T("이름 바꾸기에 실패해 원래대로 되돌렸습니다.");
                _host.ShowError(string.Join("\n", result.Errors.Take(10)));
            }
            else
            {
                Status = L.F("완료: {0}개 처리, {1}개 실패", result.Succeeded, result.Errors.Count);
                if (result.Errors.Count > 0) _host.ShowError(string.Join("\n", result.Errors.Take(10)));
            }

            if (result.OutputFolder is { } output)
            {
                _host.RefreshFolder(sequence.Directory);
                _host.ShowNotice(L.F("결과: {0}", output));
            }
            else
            {
                _host.RefreshFolder(sequence.Directory);
            }

            await LoadAsync([sequence.Directory]);
        }
        catch (OperationCanceledException)
        {
            Status = L.T("취소했습니다.");
        }
        finally
        {
            IsBusy = false;
            Progress = double.NaN;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void OpenOutput()
    {
        if (Sequence is not { } s) return;
        var folder = SequenceRenumberer.CopyFolderFor(s.Directory);
        _host.NavigateTo(Directory.Exists(folder) ? folder : s.Directory);
    }

    public void Cancel() => _cts?.Cancel();

    public void Save() => _host.SaveData(new RenumberSettings { Renumber = Renumber, StartFrame = StartFrame, Padding = Padding, Mode = Mode });
}

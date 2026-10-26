using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.Resize;

/// <summary>크기 프리셋 (file-resizer와 같은 목록 + 사용자 지정).</summary>
public sealed record ResizePreset(string Name, ResizeKind Kind, double Percent, int Width, int Height)
{
    public override string ToString() => Name;
}

/// <summary>설정에 저장하는 마지막 옵션.</summary>
public sealed class ResizeSettings
{
    public int PresetIndex { get; set; } = 1;

    public int CustomWidth { get; set; } = 1280;

    public int CustomHeight { get; set; } = 720;

    public double CustomPercent { get; set; } = 50;

    public bool KeepAspect { get; set; } = true;

    public bool NoUpscale { get; set; } = true;

    public ResizeOutput Output { get; set; } = ResizeOutput.Suffix;

    public string Suffix { get; set; } = "_resized";

    public string? OutputFolder { get; set; }

    public bool IncludeSubfolders { get; set; }

    public int JpegQuality { get; set; } = 90;
}

public sealed partial class ResizeViewModel : ObservableObject
{
    private readonly IModuleHost _host;
    private CancellationTokenSource? _cts;

    public ResizeViewModel(IModuleHost host)
    {
        _host = host;
        Presets =
        [
            new("75%", ResizeKind.Percent, 75, 0, 0),
            new("50%", ResizeKind.Percent, 50, 0, 0),
            new("25%", ResizeKind.Percent, 25, 0, 0),
            new("1920 × 1080", ResizeKind.Box, 0, 1920, 1080),
            new("1280 × 720", ResizeKind.Box, 0, 1280, 720),
            new("960 × 540", ResizeKind.Box, 0, 960, 540),
            new(L.T("사용자 지정 비율 (%)"), ResizeKind.Percent, -1, 0, 0),
            new(L.T("사용자 지정 크기"), ResizeKind.Box, 0, -1, -1),
        ];

        var s = host.LoadData<ResizeSettings>();
        _preset = Presets[Math.Clamp(s.PresetIndex, 0, Presets.Count - 1)];
        _customWidth = Math.Max(1, s.CustomWidth);
        _customHeight = Math.Max(1, s.CustomHeight);
        _customPercent = Math.Clamp(s.CustomPercent, 1, 1000);
        _keepAspect = s.KeepAspect;
        _noUpscale = s.NoUpscale;
        _output = s.Output;
        _suffix = s.Suffix;
        _outputFolder = s.OutputFolder;
        _includeSubfolders = s.IncludeSubfolders;
        _jpegQuality = Math.Clamp(s.JpegQuality, 1, 100);
    }

    public IReadOnlyList<ResizePreset> Presets { get; }

    /// <summary>처리할 파일·폴더.</summary>
    public ObservableCollection<string> Inputs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomPercent), nameof(IsCustomBox), nameof(IsBox))]
    private ResizePreset _preset;

    [ObservableProperty]
    private int _customWidth;

    [ObservableProperty]
    private int _customHeight;

    [ObservableProperty]
    private double _customPercent;

    [ObservableProperty]
    private bool _keepAspect;

    [ObservableProperty]
    private bool _noUpscale;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSuffixOutput), nameof(IsFolderOutput), nameof(IsOverwriteOutput))]
    private ResizeOutput _output;

    [ObservableProperty]
    private string _suffix;

    [ObservableProperty]
    private string? _outputFolder;

    [ObservableProperty]
    private bool _includeSubfolders;

    [ObservableProperty]
    private int _jpegQuality;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress = double.NaN;

    [ObservableProperty]
    private string _status = string.Empty;

    public bool IsCustomPercent => Preset.Percent < 0;

    public bool IsCustomBox => Preset.Width < 0;

    public bool IsBox => Preset.Kind == ResizeKind.Box;

    public bool IsSuffixOutput
    {
        get => Output == ResizeOutput.Suffix;
        set { if (value) Output = ResizeOutput.Suffix; }
    }

    public bool IsFolderOutput
    {
        get => Output == ResizeOutput.Folder;
        set { if (value) Output = ResizeOutput.Folder; }
    }

    public bool IsOverwriteOutput
    {
        get => Output == ResizeOutput.Overwrite;
        set { if (value) Output = ResizeOutput.Overwrite; }
    }

    public string InputSummary => Inputs.Count switch
    {
        0 => L.T("파일 목록에서 이미지나 폴더를 고른 뒤 우클릭 > 이미지 크기 조정, 또는 '추가'를 누르세요."),
        1 => Inputs[0],
        _ => L.F("{0}개 항목", Inputs.Count),
    };

    public void SetInputs(IEnumerable<string> paths)
    {
        Inputs.Clear();
        foreach (var p in paths.Where(p => Directory.Exists(p) || ImageFormats.IsImage(p)).Distinct(StringComparer.OrdinalIgnoreCase)) Inputs.Add(p);
        OnPropertyChanged(nameof(InputSummary));
        RunCommand.NotifyCanExecuteChanged();
        Status = string.Empty;
    }

    public void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void AddFiles()
    {
        var files = _host.PickFiles(L.T("크기를 바꿀 이미지"));
        if (files.Count > 0) SetInputs(Inputs.Concat(files));
    }

    [RelayCommand]
    private void AddFolder()
    {
        if (_host.PickFolder(L.T("크기를 바꿀 이미지가 있는 폴더"), _host.ActiveFolder) is { } folder) SetInputs(Inputs.Append(folder));
    }

    [RelayCommand]
    private void UseSelection() => SetInputs(_host.SelectedPaths);

    [RelayCommand]
    private void ClearInputs() => SetInputs([]);

    [RelayCommand]
    private void BrowseOutput()
    {
        if (_host.PickFolder(L.T("결과를 저장할 폴더"), OutputFolder ?? _host.ActiveFolder) is { } folder)
        {
            OutputFolder = folder;
            Output = ResizeOutput.Folder;
        }
    }

    private bool CanRun() => !IsBusy && Inputs.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        var options = BuildOptions();
        Save();

        var (jobs, errors) = await Task.Run(() => ImageResizer.Plan(Inputs.ToList(), options, ImageFormats.IsImage));
        if (errors.Count > 0 && jobs.Count == 0)
        {
            _host.ShowError(string.Join("\n", errors.Take(10)));
            return;
        }

        if (jobs.Count == 0)
        {
            Status = L.T("처리할 이미지가 없습니다.");
            return;
        }

        if (options.Output == ResizeOutput.Overwrite
            && !_host.Confirm(L.F("원본 이미지 {0}개를 덮어씁니다. 되돌릴 수 없습니다. 계속할까요?", jobs.Count), L.T("이미지 크기 조정")))
            return;

        IsBusy = true;
        Progress = 0;
        _cts = new CancellationTokenSource();
        var total = jobs.Count;
        var progress = new Progress<(int Done, string Name)>(p =>
        {
            Progress = p.Done / (double)total;
            Status = L.F("{0} / {1}  {2}", p.Done, total, p.Name);
        });

        try
        {
            var summary = await Task.Run(() => ImageResizer.RunAsync(jobs, options, progress, _cts.Token));
            var allErrors = errors.Concat(summary.Errors).ToList();
            var converted = jobs.Count(j => j.ConvertedToPng);
            Status = L.F("완료: {0}개 저장, {1}개 건너뜀, {2}개 실패", summary.Succeeded, summary.Skipped, allErrors.Count)
                     + (converted > 0 ? "  " + L.F("({0}개는 원본 형식으로 저장할 수 없어 PNG로 저장)", converted) : string.Empty);
            foreach (var folder in summary.TouchedFolders) _host.RefreshFolder(folder);
            if (allErrors.Count > 0) _host.ShowError(string.Join("\n", allErrors.Take(15)) + (allErrors.Count > 15 ? L.F("\n…외 {0}개", allErrors.Count - 15) : string.Empty));
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
    private void CancelRun() => Cancel();

    public ResizeOptions BuildOptions()
    {
        var kind = Preset.Kind;
        var percent = IsCustomPercent ? CustomPercent : Preset.Percent;
        var (w, h) = IsCustomBox ? (CustomWidth, CustomHeight) : (Preset.Width, Preset.Height);
        return new ResizeOptions
        {
            Kind = kind,
            Percent = percent,
            Width = Math.Max(1, w),
            Height = Math.Max(1, h),
            KeepAspect = kind == ResizeKind.Percent || KeepAspect,
            NoUpscale = NoUpscale,
            Output = Output,
            Suffix = Output == ResizeOutput.Overwrite ? string.Empty : Suffix.Trim(),
            OutputFolder = Output == ResizeOutput.Folder ? OutputFolder : null,
            IncludeSubfolders = IncludeSubfolders,
            JpegQuality = JpegQuality,
        };
    }

    public void Save() => _host.SaveData(new ResizeSettings
    {
        PresetIndex = Presets.ToList().IndexOf(Preset),
        CustomWidth = CustomWidth,
        CustomHeight = CustomHeight,
        CustomPercent = CustomPercent,
        KeepAspect = KeepAspect,
        NoUpscale = NoUpscale,
        Output = Output,
        Suffix = Suffix,
        OutputFolder = OutputFolder,
        IncludeSubfolders = IncludeSubfolders,
        JpegQuality = JpegQuality,
    });
}

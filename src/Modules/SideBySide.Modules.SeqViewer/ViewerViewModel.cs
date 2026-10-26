using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.SeqViewer;

/// <summary>레이어 하나의 표시 상태.</summary>
public sealed partial class LayerViewModel(LayerSource source) : ObservableObject
{
    public LayerSource Source { get; } = source;

    public string Name => Source.Name;

    public string Info => Source.IsSingleImage
        ? L.T("이미지 1장")
        : L.F("{0}개 프레임 · {1}–{2}", Source.FrameCount, Source.Numbers![0], Source.Numbers[^1]);

    [ObservableProperty]
    private bool _visible = true;

    [ObservableProperty]
    private double _opacity = 1;

    [ObservableProperty]
    private BlendMode _blend = BlendMode.Normal;

    [ObservableProperty]
    private bool _premultiplied;

    [ObservableProperty]
    private int _offset;

    /// <summary>범위를 벗어나도 마지막 프레임을 계속 보인다 (단일 이미지는 기본 켜짐).</summary>
    [ObservableProperty]
    private bool _holdFrame = source.IsSingleImage;

    /// <summary>전체 타임라인 프레임 → 이 레이어의 파일 순서. 보이지 않으면 -1 (seq-viewer와 같은 규칙).</summary>
    public int FileIndexAt(int frame)
    {
        var local = frame - Offset;
        if (local < 0) return -1;
        if (HoldFrame) return Math.Min(local, Source.FrameCount - 1);
        return local < Source.FrameCount ? local : -1;
    }
}

/// <summary>프리셋 안의 레이어 상태 (레이어 이름으로 맞춘다).</summary>
public sealed class PresetLayer
{
    public string Name { get; set; } = string.Empty;

    public int Order { get; set; }

    public bool Visible { get; set; } = true;

    public double Opacity { get; set; } = 1;

    public BlendMode Blend { get; set; }

    public bool Premultiplied { get; set; }

    public int Offset { get; set; }

    public bool HoldFrame { get; set; }
}

public sealed class ViewerPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public double Fps { get; set; } = 24;

    public List<PresetLayer> Layers { get; set; } = [];

    public override string ToString() => Name;
}

/// <summary>설정에 저장하는 뷰어 데이터.</summary>
public sealed class ViewerSettings
{
    public double Fps { get; set; } = 24;

    public List<ViewerPreset> Presets { get; set; } = [];

    public string? AutoApplyPresetId { get; set; }

    public bool AutoApply { get; set; }
}

public sealed partial class ViewerViewModel : ObservableObject
{
    public const int MaxLayers = 32;
    private static readonly JsonSerializerOptions ExportJson = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private readonly IModuleHost _host;
    private CancellationTokenSource? _scanCts;

    public ViewerViewModel(IModuleHost host)
    {
        _host = host;
        var s = host.LoadData<ViewerSettings>();
        _fps = Math.Clamp(s.Fps, 1, 120);
        foreach (var p in s.Presets) Presets.Add(p);
        _autoApply = s.AutoApply;
        _selectedPreset = Presets.FirstOrDefault(p => p.Id == s.AutoApplyPresetId);
    }

    /// <summary>레이어 (맨 위가 가장 위에 그려짐).</summary>
    public ObservableCollection<LayerViewModel> Layers { get; } = [];

    public ObservableCollection<ViewerPreset> Presets { get; } = [];

    public IReadOnlyList<BlendMode> BlendModes { get; } = Enum.GetValues<BlendMode>();

    /// <summary>화면을 다시 그려야 한다 (레이어 상태·프레임이 바뀜).</summary>
    public event EventHandler? RenderRequested;

    [ObservableProperty]
    private string? _folder;

    [ObservableProperty]
    private LayerViewModel? _selectedLayer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrameLabel))]
    private int _currentFrame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastFrame), nameof(FrameLabel))]
    private int _totalFrames = 1;

    [ObservableProperty]
    private double _fps;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private ViewerPreset? _selectedPreset;

    [ObservableProperty]
    private bool _autoApply;

    [ObservableProperty]
    private string _presetName = string.Empty;

    public int LastFrame => Math.Max(0, TotalFrames - 1);

    /// <summary>"12 / 240 · #1012" (맨 위 보이는 시퀀스의 파일 번호).</summary>
    public string FrameLabel
    {
        get
        {
            var label = $"{CurrentFrame + 1} / {TotalFrames}";
            var top = Layers.FirstOrDefault(l => l.Visible && l.Source.Numbers is not null && l.FileIndexAt(CurrentFrame) >= 0);
            return top is null ? label : $"{label}  ·  #{top.Source.Numbers![top.FileIndexAt(CurrentFrame)]}";
        }
    }

    public string Title => Folder is null ? L.T("시퀀스 뷰어") : Path.GetFileName(Folder.TrimEnd('\\')) is { Length: > 0 } n ? n : Folder;

    public async Task LoadAsync(IReadOnlyList<string> paths)
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        IsPlaying = false;

        // 폴더면 하위 5단계까지, 파일이면 그 폴더만 (seq-viewer와 같은 깊이)
        string? folder;
        int depth;
        if (paths.Count == 1 && Directory.Exists(paths[0]))
        {
            folder = paths[0];
            depth = 5;
        }
        else if (paths.Count > 0)
        {
            folder = Path.GetDirectoryName(paths[0]);
            depth = 0;
        }
        else
        {
            return;
        }

        if (folder is null) return;
        IsLoading = true;
        Status = L.T("시퀀스를 찾는 중…");
        try
        {
            var sources = await Task.Run(() => LayerSource.Scan(folder, depth, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;

            // 파일을 골라서 열었으면 그 파일이 속한 시퀀스를 맨 위로
            if (depth == 0)
            {
                var picked = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
                sources = sources.OrderByDescending(s => s.Files.Any(picked.Contains)).ToList();
            }

            Folder = folder;
            OnPropertyChanged(nameof(Title));
            foreach (var layer in Layers) layer.PropertyChanged -= OnLayerChanged;
            Layers.Clear();
            foreach (var source in sources.Take(MaxLayers))
            {
                var layer = new LayerViewModel(source);
                layer.PropertyChanged += OnLayerChanged;
                Layers.Add(layer);
            }

            // 원본 도구처럼 모든 레이어를 보이게 시작한다
            SelectedLayer = Layers.FirstOrDefault();
            Status = sources.Count switch
            {
                0 => L.T("이 폴더에서 이미지를 찾지 못했습니다."),
                > MaxLayers => L.F("레이어 {0}개 중 처음 {1}개만 불러왔습니다.", sources.Count, MaxLayers),
                _ => L.F("레이어 {0}개", sources.Count),
            };

            if (AutoApply && SelectedPreset is { } preset) ApplyPreset(preset);
            UpdateTotal();
            CurrentFrame = 0;
            RequestRender();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts)) IsLoading = false;
        }
    }

    private void OnLayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LayerViewModel.Offset) or nameof(LayerViewModel.HoldFrame)) UpdateTotal();
        OnPropertyChanged(nameof(FrameLabel));
        RequestRender();
    }

    partial void OnCurrentFrameChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, LastFrame);
        if (clamped != value)
        {
            CurrentFrame = clamped;
            return;
        }

        RequestRender();
    }

    partial void OnFpsChanged(double value)
    {
        if (value is < 1 or > 120 || double.IsNaN(value)) Fps = Math.Clamp(double.IsNaN(value) ? 24 : value, 1, 120);
    }

    public void RequestRender() => RenderRequested?.Invoke(this, EventArgs.Empty);

    private void UpdateTotal()
    {
        TotalFrames = Math.Max(1, Layers.Count == 0 ? 1 : Layers.Max(l => l.Source.FrameCount + l.Offset));
        if (CurrentFrame > LastFrame) CurrentFrame = LastFrame;
    }

    /// <summary>재생: 다음 프레임 (끝이면 처음으로).</summary>
    public void Advance() => CurrentFrame = CurrentFrame >= LastFrame ? 0 : CurrentFrame + 1;

    [RelayCommand]
    private void TogglePlay() => IsPlaying = !IsPlaying && TotalFrames > 1;

    [RelayCommand]
    private void Step(int delta)
    {
        IsPlaying = false;
        var next = CurrentFrame + delta;
        CurrentFrame = next < 0 ? LastFrame : next > LastFrame ? 0 : next;
    }

    [RelayCommand]
    private void GoToStart()
    {
        IsPlaying = false;
        CurrentFrame = 0;
    }

    [RelayCommand]
    private void GoToEnd()
    {
        IsPlaying = false;
        CurrentFrame = LastFrame;
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (_host.PickFolder(L.T("시퀀스가 있는 폴더"), Folder ?? _host.ActiveFolder) is { } folder) await LoadAsync([folder]);
    }

    [RelayCommand]
    private void MoveLayer(int direction)
    {
        if (SelectedLayer is not { } layer) return;
        var index = Layers.IndexOf(layer);
        var target = index + direction;
        if (target < 0 || target >= Layers.Count) return;
        Layers.Move(index, target);
        RequestRender();
    }

    [RelayCommand]
    private void ToggleVisible(LayerViewModel? layer)
    {
        if (layer is not null) layer.Visible = !layer.Visible;
    }

    // ── 프리셋 ──────────────────────────────────────────────────

    [RelayCommand]
    private void SavePreset()
    {
        var name = string.IsNullOrWhiteSpace(PresetName) ? L.F("프리셋 {0}", Presets.Count + 1) : PresetName.Trim();
        var existing = Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        var preset = existing ?? new ViewerPreset { Name = name };
        preset.Fps = Fps;
        preset.Layers = Layers.Select((l, i) => new PresetLayer
        {
            Name = l.Name,
            Order = i,
            Visible = l.Visible,
            Opacity = l.Opacity,
            Blend = l.Blend,
            Premultiplied = l.Premultiplied,
            Offset = l.Offset,
            HoldFrame = l.HoldFrame,
        }).ToList();
        if (existing is null) Presets.Add(preset);
        SelectedPreset = preset;
        PresetName = string.Empty;
        Save();
        Status = L.F("프리셋 '{0}'을(를) 저장했습니다.", preset.Name);
    }

    [RelayCommand]
    private void ApplySelectedPreset()
    {
        if (SelectedPreset is { } preset) ApplyPreset(preset);
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset is not { } preset) return;
        Presets.Remove(preset);
        SelectedPreset = null;
        Save();
    }

    [RelayCommand]
    private void ExportPresets()
    {
        var dialog = new SaveFileDialog { Title = L.T("프리셋 내보내기"), Filter = "JSON (*.json)|*.json", FileName = "seqviewer-presets.json" };
        if (dialog.ShowDialog(_host.Owner) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(Presets.ToList(), ExportJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.ShowError(ex.Message);
        }
    }

    [RelayCommand]
    private void ImportPresets()
    {
        var dialog = new OpenFileDialog { Title = L.T("프리셋 가져오기"), Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog(_host.Owner) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 1024 * 1024) throw new InvalidDataException(L.T("파일이 너무 큽니다."));
            var imported = JsonSerializer.Deserialize<List<ViewerPreset>>(File.ReadAllText(dialog.FileName), ExportJson) ?? [];
            foreach (var p in imported.Where(p => !string.IsNullOrWhiteSpace(p.Name)).Take(100))
            {
                p.Id = Guid.NewGuid().ToString("N");
                p.Layers = (p.Layers ?? []).Take(MaxLayers).ToList();
                p.Fps = Math.Clamp(p.Fps, 1, 120);
                Presets.Add(p);
            }

            Save();
            Status = L.F("프리셋 {0}개를 가져왔습니다.", imported.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        {
            _host.ShowError(ex.Message);
        }
    }

    /// <summary>이름이 같은 레이어에 상태를 적용하고 순서를 맞춘다 (없는 레이어는 그대로).</summary>
    public void ApplyPreset(ViewerPreset preset)
    {
        Fps = Math.Clamp(preset.Fps, 1, 120);
        var byName = preset.Layers.GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var layer in Layers)
        {
            if (!byName.TryGetValue(layer.Name, out var p)) continue;
            layer.Visible = p.Visible;
            layer.Opacity = Math.Clamp(p.Opacity, 0, 1);
            layer.Blend = Enum.IsDefined(p.Blend) ? p.Blend : BlendMode.Normal;
            layer.Premultiplied = p.Premultiplied;
            layer.Offset = Math.Clamp(p.Offset, -100000, 100000);
            layer.HoldFrame = p.HoldFrame;
        }

        var ordered = Layers.OrderBy(l => byName.TryGetValue(l.Name, out var p) ? p.Order : int.MaxValue).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var from = Layers.IndexOf(ordered[i]);
            if (from != i) Layers.Move(from, i);
        }

        UpdateTotal();
        RequestRender();
    }

    partial void OnSelectedPresetChanged(ViewerPreset? value) => Save();

    partial void OnAutoApplyChanged(bool value) => Save();

    public void Save() => _host.SaveData(new ViewerSettings
    {
        Fps = Fps,
        Presets = Presets.ToList(),
        AutoApplyPresetId = SelectedPreset?.Id,
        AutoApply = AutoApply,
    });

    public void Stop()
    {
        _scanCts?.Cancel();
        IsPlaying = false;
    }
}

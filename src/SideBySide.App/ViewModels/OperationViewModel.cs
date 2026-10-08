using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SideBySide.App.Infrastructure;
using SideBySide.Core.FileSystem;

namespace SideBySide.App.ViewModels;

/// <summary>
/// 진행 중인 파일 작업 하나 (복사·이동·삭제). 하단 작업 줄, 주소 표시줄 채움, 작업 표시줄 진행률에 쓰인다.
/// </summary>
public sealed partial class OperationViewModel : ObservableObject
{
    private readonly CancellationTokenSource? _cts;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _currentItem = string.Empty;

    [ObservableProperty]
    private string _details = string.Empty;

    /// <summary>0..1</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    private double _fraction;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    private bool _isIndeterminate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isDone;

    public OperationViewModel(string title, string? destinationDirectory, CancellationTokenSource? cts)
    {
        _title = title;
        DestinationDirectory = destinationDirectory;
        _cts = cts;
        _isIndeterminate = cts is null;
    }

    /// <summary>주소 표시줄 진행률을 표시할 대상 폴더 (삭제는 원본 폴더).</summary>
    public string? DestinationDirectory { get; }

    public CancellationToken Token => _cts?.Token ?? CancellationToken.None;

    public bool CanCancel => _cts is not null;

    public string PercentText => IsIndeterminate ? string.Empty : $"{Fraction * 100:0}%";

    public void Update(TransferProgress p)
    {
        IsIndeterminate = false;
        Fraction = p.Fraction;
        CurrentItem = p.CurrentItem;
        var parts = new List<string>
        {
            $"{p.FilesDone:N0} / {p.FilesTotal:N0}개",
            $"{Formatting.Size(p.BytesDone)} / {Formatting.Size(p.BytesTotal)}",
        };
        if (p.BytesPerSecond > 1024 && p.BytesDone < p.BytesTotal) parts.Add($"{Formatting.Size((long)p.BytesPerSecond)}/s");
        if (p.Remaining is { } remaining && remaining.TotalSeconds >= 1) parts.Add(FormatRemaining(remaining));
        Details = string.Join(" · ", parts);
    }

    public void Finish(string summary)
    {
        IsDone = true;
        IsIndeterminate = false;
        Fraction = 1;
        CurrentItem = string.Empty;
        Details = summary;
    }

    [RelayCommand(CanExecute = nameof(CanExecuteCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
        Details = "취소하는 중…";
    }

    private bool CanExecuteCancel() => CanCancel && !IsDone;

    private static string FormatRemaining(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}시간 {t.Minutes}분 남음"
         : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}분 {t.Seconds}초 남음"
         : $"{t.Seconds}초 남음";
}

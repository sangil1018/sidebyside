using CommunityToolkit.Mvvm.ComponentModel;
using SideBySide.App.Infrastructure;
using SideBySide.App.Services;
using SideBySide.Core.Compare;
using SideBySide.Core.Diff;

namespace SideBySide.App.ViewModels;

/// <summary>파일 비교 화면의 한 줄 (좌우 한 쌍).</summary>
public sealed class DiffLineViewModel(DiffLine line, int row)
{
    public DiffLine Line { get; } = line;

    public int Row { get; } = row;

    public DiffLineKind Kind => Line.Kind;

    public int HunkIndex => Line.HunkIndex;

    public string LeftNumber => Line.LeftIndex is int i ? (i + 1).ToString() : string.Empty;

    public string RightNumber => Line.RightIndex is int i ? (i + 1).ToString() : string.Empty;

    public string LeftText => Line.LeftText ?? string.Empty;

    public string RightText => Line.RightText ?? string.Empty;

    public bool HasLeft => Line.LeftText is not null;

    public bool HasRight => Line.RightText is not null;

    public IReadOnlyList<TextSpan> LeftSpans => Line.LeftSpans;

    public IReadOnlyList<TextSpan> RightSpans => Line.RightSpans;
}

/// <summary>
/// 두 텍스트 파일 비교/병합. 차이 블록 단위 적용(Alt+←/→), 전체 텍스트 편집(Ctrl+E), 실행 취소, 저장을 지원한다.
/// 저장 시 원본 인코딩·BOM·줄끝을 유지한다.
/// </summary>
public sealed partial class FileDiffViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly Action<DiffOptions> _saveOptions;
    private DiffDocument? _document;
    private TextFileFormat? _leftFormat;
    private TextFileFormat? _rightFormat;

    [ObservableProperty]
    private DiffLineViewModel? _selectedLine;

    [ObservableProperty]
    private bool _isBinary;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _leftText = string.Empty;

    [ObservableProperty]
    private string _rightText = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _ignoreWhitespace;

    [ObservableProperty]
    private bool _ignoreCase;

    [ObservableProperty]
    private string _leftInfo = string.Empty;

    [ObservableProperty]
    private string _rightInfo = string.Empty;

    [ObservableProperty]
    private bool _leftDirty;

    [ObservableProperty]
    private bool _rightDirty;

    private FileDiffViewModel(string leftPath, string rightPath, DiffOptions options, IDialogService dialogs, Action<DiffOptions> saveOptions)
    {
        LeftPath = leftPath;
        RightPath = rightPath;
        _dialogs = dialogs;
        _saveOptions = saveOptions;
        _ignoreWhitespace = options.IgnoreWhitespace;
        _ignoreCase = options.IgnoreCase;
    }

    public string LeftPath { get; }

    public string RightPath { get; }

    public string LeftName => Path.GetFileName(LeftPath);

    public string RightName => Path.GetFileName(RightPath);

    public BulkObservableCollection<DiffLineViewModel> Lines { get; } = [];

    public bool IsText => !IsBinary && Message.Length == 0;

    public bool CanEdit => _document is not null;

    /// <summary>뷰가 해당 행으로 스크롤하도록 요청.</summary>
    public event EventHandler<int>? ScrollRequested;

    public static async Task<FileDiffViewModel> LoadAsync(
        string leftPath, string rightPath, DiffOptions options, IDialogService dialogs, Action<DiffOptions> saveOptions)
    {
        var vm = new FileDiffViewModel(leftPath, rightPath, options, dialogs, saveOptions);
        try
        {
            var (left, right) = await Task.Run(() => (TextFileCodec.Read(leftPath), TextFileCodec.Read(rightPath)));
            vm.LeftInfo = Describe(left);
            vm.RightInfo = Describe(right);

            if (left.IsBinary || right.IsBinary)
            {
                var equal = await FileContentComparer.AreEqualAsync(leftPath, rightPath);
                vm.IsBinary = true;
                vm.Message = equal
                    ? "바이너리 파일 — 두 파일의 내용이 같습니다."
                    : $"바이너리 파일 — 내용이 다릅니다. ({Formatting.Size(left.Size)} / {Formatting.Size(right.Size)})";
                vm.Summary = equal ? "같음" : "다름";
                return vm;
            }

            vm._leftFormat = left.Format;
            vm._rightFormat = right.Format;
            vm._document = await Task.Run(() => new DiffDocument(left.Lines, right.Lines, options));
            vm._document.Changed += (_, _) => vm.Rebuild();
            vm.Rebuild();
            if (vm._document.Result.Hunks.Count > 0) vm.GoToHunk(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            vm.Message = ex.Message;
        }

        return vm;
    }

    public void Next() => Navigate(forward: true);

    public void Previous() => Navigate(forward: false);

    public void Apply(DiffSide target)
    {
        if (_document is null || IsEditing) return;
        var hunk = CurrentHunk();
        if (hunk < 0) return;
        var row = _document.Result.Hunks[hunk].FirstRow;
        _document.ApplyHunk(hunk, target);

        // 적용 후 같은 위치 다음 차이로 이동
        var next = _document.FindHunk(row - 1, forward: true);
        if (next >= 0) GoToHunk(next);
    }

    public void ApplyAll(DiffSide target)
    {
        if (_document is null || IsEditing) return;
        _document.ApplyAll(target);
    }

    public void Undo()
    {
        if (IsEditing) return;
        _document?.Undo();
    }

    public void ToggleEdit()
    {
        if (_document is null) return;
        if (!IsEditing)
        {
            LeftText = string.Join("\r\n", _document.Left);
            RightText = string.Join("\r\n", _document.Right);
            IsEditing = true;
            return;
        }

        CommitEdit();
    }

    public bool Save()
    {
        if (_document is null) return true;
        if (IsEditing) CommitEdit();

        try
        {
            if (_document.LeftDirty)
            {
                TextFileCodec.Write(LeftPath, _document.Left, _leftFormat!);
                _document.MarkSaved(DiffSide.Left);
            }

            if (_document.RightDirty)
            {
                TextFileCodec.Write(RightPath, _document.Right, _rightFormat!);
                _document.MarkSaved(DiffSide.Right);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("저장하지 못했습니다.\n" + ex.Message);
            return false;
        }
    }

    /// <summary>저장 안 된 변경이 있으면 묻는다. 닫아도 되면 true.</summary>
    public bool ConfirmClose()
    {
        if (_document is null) return true;
        if (IsEditing) CommitEdit();
        if (!_document.LeftDirty && !_document.RightDirty) return true;

        var answer = _dialogs.AskYesNoCancel("변경 내용을 저장할까요?", "파일 비교");
        return answer switch
        {
            true => Save(),
            false => true,
            null => false,
        };
    }

    partial void OnIgnoreWhitespaceChanged(bool value) => OptionsChanged();

    partial void OnIgnoreCaseChanged(bool value) => OptionsChanged();

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(IsText));

    partial void OnIsBinaryChanged(bool value) => OnPropertyChanged(nameof(IsText));

    private void OptionsChanged()
    {
        var options = new DiffOptions { IgnoreWhitespace = IgnoreWhitespace, IgnoreCase = IgnoreCase };
        _saveOptions(options);
        _document?.SetOptions(options);
    }

    private void CommitEdit()
    {
        if (_document is null) return;
        IsEditing = false;
        _document.ReplaceText(DiffSide.Left, TextFileCodec.SplitLines(LeftText).Lines);
        _document.ReplaceText(DiffSide.Right, TextFileCodec.SplitLines(RightText).Lines);
    }

    private int CurrentHunk()
    {
        if (_document is null || _document.Result.Hunks.Count == 0) return -1;
        if (SelectedLine is { HunkIndex: >= 0 } line) return line.HunkIndex;
        var row = SelectedLine?.Row ?? -1;
        var next = _document.FindHunk(row, forward: true);
        return next >= 0 ? next : _document.FindHunk(row + 1, forward: false);
    }

    private void Navigate(bool forward)
    {
        if (_document is null) return;
        var row = SelectedLine?.Row ?? -1;
        if (!forward && SelectedLine is { HunkIndex: >= 0 } current) row = _document.Result.Hunks[current.HunkIndex].FirstRow;
        if (forward && SelectedLine is { HunkIndex: >= 0 } cur)
        {
            var h = _document.Result.Hunks[cur.HunkIndex];
            row = h.FirstRow + h.RowCount - 1;
        }

        var hunk = _document.FindHunk(row, forward);
        if (hunk >= 0) GoToHunk(hunk);
    }

    private void GoToHunk(int hunk)
    {
        if (_document is null || hunk < 0 || hunk >= _document.Result.Hunks.Count) return;
        var row = _document.Result.Hunks[hunk].FirstRow;
        if (row >= Lines.Count) return;
        SelectedLine = Lines[row];
        ScrollRequested?.Invoke(this, row);
    }

    private void Rebuild()
    {
        if (_document is null) return;
        var selectedRow = SelectedLine?.Row ?? 0;
        var result = _document.Result;
        Lines.ReplaceAll(result.Lines.Select((l, i) => new DiffLineViewModel(l, i)));
        SelectedLine = Lines.Count == 0 ? null : Lines[Math.Min(selectedRow, Lines.Count - 1)];
        LeftDirty = _document.LeftDirty;
        RightDirty = _document.RightDirty;
        Summary = result.AreEqual
            ? "같음"
            : $"차이 {result.Hunks.Count}곳";
        OnPropertyChanged(nameof(CanEdit));
    }

    private static string Describe(TextFileContent content)
        => content.IsBinary
            ? $"바이너리 · {Formatting.Size(content.Size)}"
            : $"{content.Format.EncodingName} · {content.Format.NewLineName} · {content.Lines.Count:N0}줄";
}

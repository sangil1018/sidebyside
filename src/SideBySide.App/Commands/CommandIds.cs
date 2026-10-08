namespace SideBySide.App.Commands;

/// <summary>앱의 모든 명령 ID. 새 명령은 여기와 <see cref="CommandRegistry"/>에 한 줄씩 추가하고 MainViewModel에서 동작을 등록한다.</summary>
public static class CommandIds
{
    // 모드·레이아웃
    public const string ToggleMode = nameof(ToggleMode);
    public const string ToggleDualPane = nameof(ToggleDualPane);
    public const string TogglePreview = nameof(TogglePreview);
    public const string ToggleSidebar = nameof(ToggleSidebar);
    public const string ToggleHidden = nameof(ToggleHidden);
    public const string ToggleTheme = nameof(ToggleTheme);
    public const string SwitchPane = nameof(SwitchPane);
    public const string ViewDetails = nameof(ViewDetails);
    public const string ViewMediumIcons = nameof(ViewMediumIcons);
    public const string ViewLargeIcons = nameof(ViewLargeIcons);

    // 탭
    public const string NewTab = nameof(NewTab);
    public const string CloseTab = nameof(CloseTab);
    public const string NextTab = nameof(NextTab);
    public const string PrevTab = nameof(PrevTab);

    // 탐색
    public const string Back = nameof(Back);
    public const string Forward = nameof(Forward);
    public const string Up = nameof(Up);
    public const string Refresh = nameof(Refresh);
    public const string FocusAddress = nameof(FocusAddress);
    public const string FocusSearch = nameof(FocusSearch);
    public const string SearchSubfolders = nameof(SearchSubfolders);
    public const string Cancel = nameof(Cancel);

    // 파일 작업
    public const string Open = nameof(Open);
    public const string Undo = nameof(Undo);
    public const string Rename = nameof(Rename);
    public const string Delete = nameof(Delete);
    public const string DeletePermanent = nameof(DeletePermanent);
    public const string Copy = nameof(Copy);
    public const string Cut = nameof(Cut);
    public const string Paste = nameof(Paste);
    public const string NewFolder = nameof(NewFolder);
    public const string NewTextFile = nameof(NewTextFile);
    public const string Properties = nameof(Properties);
    public const string CopyPath = nameof(CopyPath);
    public const string ShowContextMenu = nameof(ShowContextMenu);
    public const string OpenTerminal = nameof(OpenTerminal);
    public const string PinFavorite = nameof(PinFavorite);
    public const string CopyToRight = nameof(CopyToRight);
    public const string CopyToLeft = nameof(CopyToLeft);

    // 필터
    public const string EditFilter = nameof(EditFilter);
    public const string ClearFilter = nameof(ClearFilter);

    // 비교
    public const string NextDifference = nameof(NextDifference);
    public const string PrevDifference = nameof(PrevDifference);
    public const string ToggleDifferencesOnly = nameof(ToggleDifferencesOnly);
    public const string CompareSelectedFiles = nameof(CompareSelectedFiles);
    public const string SaveSession = nameof(SaveSession);
    public const string OpenSession = nameof(OpenSession);

    // 파일 비교 화면
    public const string DiffNext = nameof(DiffNext);
    public const string DiffPrev = nameof(DiffPrev);
    public const string DiffApplyToRight = nameof(DiffApplyToRight);
    public const string DiffApplyToLeft = nameof(DiffApplyToLeft);
    public const string DiffSave = nameof(DiffSave);
    public const string DiffUndo = nameof(DiffUndo);
    public const string DiffToggleEdit = nameof(DiffToggleEdit);
    public const string DiffClose = nameof(DiffClose);
}

using SideBySide.Core.Filtering;
using SideBySide.Core.Settings;
using SideBySide.Shell.Abstractions;

namespace SideBySide.App.Services;

/// <summary>
/// 모든 탭이 공유하는 상태와 서비스 묶음 (필터, 숨김 항목 표시, 셸 서비스).
/// 필터가 바뀌면 <see cref="FilterChanged"/>로 알리고 각 탭은 디스크를 다시 읽지 않고 재필터링한다.
/// </summary>
public sealed class ExplorerContext(
    AppSettings settings,
    IIconProvider icons,
    IFileOperations fileOperations,
    IFileClipboard clipboard,
    IContextMenuService contextMenu,
    ILauncher launcher,
    IDialogService dialogs)
{
    private FilterSet _filter = FilterSet.Create(settings.Filter, settings.ShowHidden);

    public AppSettings Settings { get; } = settings;

    public IIconProvider Icons { get; } = icons;

    public IFileOperations FileOperations { get; } = fileOperations;

    public IFileClipboard Clipboard { get; } = clipboard;

    public IContextMenuService ContextMenu { get; } = contextMenu;

    public ILauncher Launcher { get; } = launcher;

    public IDialogService Dialogs { get; } = dialogs;

    public FilterSet Filter => _filter;

    /// <summary>잘라내기 표시(반투명)를 위한 현재 잘라내기 대상.</summary>
    public HashSet<string> CutPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? FilterChanged;

    public event EventHandler? CutPathsChanged;

    public void SetFilter(FilterSettings filter)
    {
        Settings.Filter = filter;
        Rebuild();
    }

    public void SetShowHidden(bool show)
    {
        Settings.ShowHidden = show;
        Rebuild();
    }

    public void SetCut(IEnumerable<string> paths)
    {
        CutPaths.Clear();
        foreach (var p in paths) CutPaths.Add(p);
        CutPathsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Rebuild()
    {
        _filter = FilterSet.Create(Settings.Filter, Settings.ShowHidden);
        FilterChanged?.Invoke(this, EventArgs.Empty);
    }
}

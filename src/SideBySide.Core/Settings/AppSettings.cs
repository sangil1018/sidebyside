using SideBySide.Core.Compare;
using SideBySide.Core.Diff;
using SideBySide.Core.Filtering;
using SideBySide.Core.Sessions;

namespace SideBySide.Core.Settings;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>파일 목록 보기 방식. 비교 모드는 항상 자세히 보기.</summary>
public enum ViewMode
{
    Details,
    MediumIcons,
    LargeIcons,
}

public sealed class WindowPlacement
{
    public double Left { get; set; } = double.NaN;

    public double Top { get; set; } = double.NaN;

    public double Width { get; set; } = 1280;

    public double Height { get; set; } = 800;

    public bool Maximized { get; set; }
}

public sealed class PaneState
{
    public List<string> Tabs { get; set; } = [];

    public int ActiveTab { get; set; }
}

/// <summary>
/// 앱 전체 설정. 필드를 추가할 때는 기본값을 꼭 지정하고,
/// 형식이 바뀌면 <see cref="CurrentVersion"/>을 올리고 <see cref="JsonSettingsStore"/>에 마이그레이션을 추가한다.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    public WindowPlacement Window { get; set; } = new();

    /// <summary>두 창(= 비교 모드)으로 시작할지. 기본은 한 창 탐색기.</summary>
    public bool DualPane { get; set; }

    public bool ShowHidden { get; set; }

    public ViewMode ViewMode { get; set; } = ViewMode.Details;

    public bool ShowPreview { get; set; }

    public bool ShowSidebar { get; set; } = true;

    public double SidebarWidth { get; set; } = 210;

    public double PreviewWidth { get; set; } = 340;

    public PaneState Left { get; set; } = new();

    public PaneState Right { get; set; } = new();

    public FilterSettings Filter { get; set; } = FilterSettings.None;

    public List<FilterSettings> FilterPresets { get; set; } =
    [
        new() { Name = "빌드 산출물", Exclude = @"bin\; obj\; *.pdb; *.obj; *.o; *.cache" },
        new() { Name = "버전 관리·IDE", Exclude = @".git\; .svn\; .hg\; .vs\; .idea\; node_modules\" },
        new() { Name = "임시 파일", Exclude = "*.tmp; *.bak; ~$*; Thumbs.db; desktop.ini; .DS_Store" },
    ];

    public List<CompareSession> Sessions { get; set; } = [];

    public CompareOptions Compare { get; set; } = new();

    public DiffOptions Diff { get; set; } = new();

    public bool ShowDifferencesOnly { get; set; }

    public List<string> Favorites { get; set; } = [];
}

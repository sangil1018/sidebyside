using SideBySide.Core.Compare;
using SideBySide.Core.Diff;
using SideBySide.Core.Filtering;
using SideBySide.Core.Sessions;

namespace SideBySide.Core.Settings;

/// <summary>화면 언어. 바꾸면 다시 시작할 때 적용된다.</summary>
public enum AppLanguage
{
    Korean,
    English,
}

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>탐색 창 빠른 실행에 표시할 수 있는 시스템 위치 (앱이 직접 목록을 보여줄 수 없어 Windows로 연다).</summary>
public enum SystemPlace
{
    RecycleBin,
    ControlPanel,
    Network,
    WindowsSettings,
}

/// <summary>Win 조합으로 SideBySide 창을 앞으로 가져오는 단축키. 셸이 예약하지 않은 조합만 제공한다.</summary>
public enum OpenShortcut
{
    WinAltE,
    WinCtrlE,
    WinShiftE,
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

    public AppLanguage Language { get; set; } = AppLanguage.Korean;

    /// <summary>앱이 켜져 있을 때 창을 앞으로 가져오는 전역 단축키.</summary>
    public OpenShortcut OpenShortcut { get; set; } = OpenShortcut.WinAltE;

    public WindowPlacement Window { get; set; } = new();

    /// <summary>두 창으로 시작할지. 기본은 한 창 탐색기.</summary>
    public bool DualPane { get; set; }

    /// <summary>두 창이 켜져 있을 때 비교도 켠 채로 시작할지.</summary>
    public bool CompareMode { get; set; }

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

    /// <summary>빠른 실행에 표시할 시스템 위치. 기본은 휴지통.</summary>
    public List<SystemPlace> SystemPlaces { get; set; } = [SystemPlace.RecycleBin];

    /// <summary>사용자가 바꾼 단축키 (명령 ID → "Ctrl+Shift+E" 형식, 빈 문자열 = 단축키 없음). 없는 명령은 기본값.</summary>
    public Dictionary<string, string> KeyBindings { get; set; } = [];
}

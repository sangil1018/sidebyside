using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SideBySide.App.Infrastructure;
using SideBySide.App.Services;
using SideBySide.Core.Settings;
using SideBySide.Shell.Abstractions;
using SideBySide.Core.Localization;

namespace SideBySide.App.ViewModels;

public sealed class SidebarItem
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    /// <summary>Segoe Fluent Icons 글리프.</summary>
    public string Glyph { get; init; } = "";

    public bool IsPinned { get; init; }

    public double Usage { get; init; } = -1;

    public string Details { get; init; } = string.Empty;

    public bool HasUsage => Usage >= 0;

    /// <summary>USB 장치·광학 드라이브처럼 안전하게 제거할 수 있는 드라이브.</summary>
    public bool CanEject { get; init; }

    /// <summary>휴지통 같은 시스템 위치: 앱 안에서 탐색하지 않고 Windows로 연다.</summary>
    public bool IsSystemPlace { get; init; }
}

/// <summary>왼쪽 탐색 창: 빠른 실행(알려진 폴더 + 고정 항목)과 드라이브 목록.</summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    private readonly ExplorerContext _ctx;
    private readonly IDriveService _drives;

    public SidebarViewModel(ExplorerContext ctx, IDriveService drives)
    {
        _ctx = ctx;
        _drives = drives;
        RebuildQuickAccess();
    }

    public ObservableCollection<SidebarItem> QuickAccess { get; } = [];

    public ObservableCollection<SidebarItem> Drives { get; } = [];

    public void Pin(string path)
    {
        if (string.IsNullOrEmpty(path) || _ctx.Settings.Favorites.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        _ctx.Settings.Favorites.Add(path);
        RebuildQuickAccess();
    }

    public void Unpin(string path)
    {
        _ctx.Settings.Favorites.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        RebuildQuickAccess();
    }

    public async Task RefreshDrivesAsync()
    {
        var items = await Task.Run(() => DriveInfo.GetDrives().Select(d =>
        {
            double usage = -1;
            var details = string.Empty;
            try
            {
                if (d.IsReady && d.TotalSize > 0)
                {
                    usage = 1 - (double)d.AvailableFreeSpace / d.TotalSize;
                    details = L.F("{0} 남음", Formatting.Size(d.AvailableFreeSpace));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return new SidebarItem
            {
                Name = _ctx.Icons.GetDisplayName(d.Name),
                Path = d.Name,
                Glyph = d.DriveType switch
                {
                    DriveType.Network => "",
                    DriveType.Removable => "",
                    DriveType.CDRom => "",
                    _ => "",
                },
                Usage = usage,
                Details = details,
                CanEject = _drives.IsEjectable(d.Name),
            };
        }).ToList());

        Drives.Clear();
        foreach (var item in items) Drives.Add(item);
    }

    /// <summary>표시할 수 있는 시스템 위치: (종류, 이름, 셸 대상, 글리프).</summary>
    public static IReadOnlyList<(SystemPlace Place, string Name, string Target, string Glyph)> SystemPlaces { get; } =
    [
        (SystemPlace.RecycleBin, L.T("휴지통"), "shell:RecycleBinFolder", "\uE74D"),
        (SystemPlace.ControlPanel, L.T("제어판"), "shell:ControlPanelFolder", "\uE912"),
        (SystemPlace.Network, L.T("네트워크"), "shell:NetworkPlacesFolder", "\uE968"),
        (SystemPlace.WindowsSettings, L.T("Windows 설정"), "ms-settings:", "\uE713"),
    ];

    /// <summary>설정이 바뀌면 빠른 실행을 다시 만든다.</summary>
    public void Refresh() => RebuildQuickAccess();

    private void RebuildQuickAccess()
    {
        QuickAccess.Clear();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var known = new (string Name, string Path, string Glyph)[]
        {
            (L.T("바탕 화면"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ""),
            (L.T("다운로드"), Path.Combine(profile, "Downloads"), ""),
            (L.T("문서"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ""),
            (L.T("사진"), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), ""),
            (L.T("음악"), Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), ""),
            (L.T("동영상"), Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), ""),
        };

        foreach (var (name, path, glyph) in known)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                QuickAccess.Add(new SidebarItem { Name = name, Path = path, Glyph = glyph });
        }

        foreach (var (place, name, target, glyph) in SystemPlaces)
        {
            if (_ctx.Settings.SystemPlaces.Contains(place))
                QuickAccess.Add(new SidebarItem { Name = name, Path = target, Glyph = glyph, IsSystemPlace = true });
        }

        foreach (var fav in _ctx.Settings.Favorites)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(fav));
            QuickAccess.Add(new SidebarItem { Name = name.Length > 0 ? name : fav, Path = fav, Glyph = "", IsPinned = true });
        }
    }
}

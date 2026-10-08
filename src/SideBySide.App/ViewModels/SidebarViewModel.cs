using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SideBySide.App.Infrastructure;
using SideBySide.App.Services;

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
}

/// <summary>왼쪽 탐색 창: 빠른 실행(알려진 폴더 + 고정 항목)과 드라이브 목록.</summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    private readonly ExplorerContext _ctx;

    public SidebarViewModel(ExplorerContext ctx)
    {
        _ctx = ctx;
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
                    details = $"{Formatting.Size(d.AvailableFreeSpace)} 남음";
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
            };
        }).ToList());

        Drives.Clear();
        foreach (var item in items) Drives.Add(item);
    }

    private void RebuildQuickAccess()
    {
        QuickAccess.Clear();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var known = new (string Name, string Path, string Glyph)[]
        {
            ("바탕 화면", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ""),
            ("다운로드", Path.Combine(profile, "Downloads"), ""),
            ("문서", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ""),
            ("사진", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), ""),
            ("음악", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), ""),
            ("동영상", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), ""),
        };

        foreach (var (name, path, glyph) in known)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                QuickAccess.Add(new SidebarItem { Name = name, Path = path, Glyph = glyph });
        }

        foreach (var fav in _ctx.Settings.Favorites)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(fav));
            QuickAccess.Add(new SidebarItem { Name = name.Length > 0 ? name : fav, Path = fav, Glyph = "", IsPinned = true });
        }
    }
}

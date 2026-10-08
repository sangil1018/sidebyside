using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SideBySide.App.Infrastructure;
using SideBySide.Core.Compare;
using SideBySide.Core.FileSystem;
using SideBySide.Shell.Abstractions;

namespace SideBySide.App.ViewModels;

/// <summary>
/// 파일 목록 한 행. 아이콘·형식 이름은 화면에 보일 때(가상화로 컨테이너가 만들어질 때) 처음 요청된다.
/// 비교 모드에서는 반대쪽에 없는 항목 자리에 빈 행(<see cref="IsPlaceholder"/>)이 들어간다.
/// </summary>
public sealed partial class FileItemViewModel : ObservableObject
{
    private readonly IIconProvider _icons;
    private ImageSource? _icon;
    private bool _iconRequested;
    private ImageSource? _mediumImage;
    private bool _mediumRequested;
    private ImageSource? _largeImage;
    private bool _largeRequested;
    private string? _typeText;

    [ObservableProperty]
    private bool _isCut;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private CompareState? _compareState;

    [ObservableProperty]
    private bool _isNewer;

    public FileItemViewModel(FileEntry entry, IIconProvider icons)
    {
        Entry = entry;
        _icons = icons;
        Name = entry.Name;
        IsDirectory = entry.IsDirectory;
    }

    private FileItemViewModel(string name, bool isDirectory, IIconProvider icons)
    {
        _icons = icons;
        Name = name;
        IsDirectory = isDirectory;
    }

    public FileEntry? Entry { get; }

    public bool IsPlaceholder => Entry is null;

    public string Name { get; }

    /// <summary>드라이브 목록에서 "로컬 디스크 (C:)"처럼 표시할 이름. 없으면 Name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    public string Label => IsPlaceholder ? string.Empty : DisplayName.Length > 0 ? DisplayName : Name;

    public bool IsDirectory { get; }

    public bool IsDrive { get; init; }

    /// <summary>드라이브 사용률 0..1 (드라이브 행 전용).</summary>
    public double DriveUsage { get; init; }

    public string DriveSpaceText { get; init; } = string.Empty;

    public string FullPath => Entry?.FullPath ?? string.Empty;

    public long Size => Entry?.Size ?? 0;

    public DateTime Modified => Entry?.LastWriteTimeUtc ?? DateTime.MinValue;

    public bool IsHidden => Entry?.IsHidden == true;

    public string SizeText => IsDrive ? DriveSpaceText : Entry is null || IsDirectory ? string.Empty : Formatting.SizeInKb(Entry.Size);

    public string ModifiedText => Entry is null || IsDrive ? string.Empty : Formatting.Date(Entry.LastWriteTimeUtc);

    public string TypeText => Entry is null ? string.Empty : _typeText ??= IsDrive ? "드라이브" : _icons.GetTypeName(Entry.FullPath, IsDirectory);

    /// <summary>하위 폴더 검색 결과에서 검색 시작 폴더 기준 상대 위치 (예: "src\\Views").</summary>
    public string Location { get; init; } = string.Empty;

    /// <summary>중간 아이콘 보기 (48px).</summary>
    public ImageSource? MediumImage => LoadImage(48, ref _mediumImage, ref _mediumRequested, nameof(MediumImage));

    /// <summary>큰 아이콘 보기 (96px, 사진·동영상은 썸네일).</summary>
    public ImageSource? LargeImage => LoadImage(96, ref _largeImage, ref _largeRequested, nameof(LargeImage));

    /// <summary>비교 모드에서 반대편 같은 행 항목.</summary>
    public FileItemViewModel? Partner { get; set; }

    public CompareRow? Row { get; set; }

    public ImageSource? Icon
    {
        get
        {
            if (_iconRequested || Entry is null) return _icon;
            _iconRequested = true;
            _icon = _icons.TryGetCached(Entry.FullPath, IsDirectory);
            if (_icon is null)
            {
                _icons.Request(Entry.FullPath, IsDirectory, icon =>
                {
                    _icon = icon;
                    OnPropertyChanged(nameof(Icon));
                });
            }

            return _icon;
        }
    }

    private ImageSource? LoadImage(int size, ref ImageSource? field, ref bool requested, string propertyName)
    {
        if (requested || Entry is null) return field;
        requested = true;
        field = _icons.TryGetCachedImage(Entry.FullPath, size);
        if (field is null)
        {
            _icons.RequestImage(Entry.FullPath, size, image =>
            {
                if (size == 48) _mediumImage = image;
                else _largeImage = image;
                OnPropertyChanged(propertyName);
            });
        }

        return field;
    }

    public static FileItemViewModel Placeholder(string name, bool isDirectory, IIconProvider icons)
        => new(name, isDirectory, icons);
}

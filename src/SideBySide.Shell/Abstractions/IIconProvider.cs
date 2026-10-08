using System.Windows.Media;

namespace SideBySide.Shell.Abstractions;

/// <summary>시스템 아이콘/형식 이름 제공. 결과는 캐시되고 ImageSource는 Freeze 되어 있다.</summary>
public interface IIconProvider
{
    /// <summary>캐시에 있으면 즉시 반환, 없으면 null.</summary>
    ImageSource? TryGetCached(string path, bool isDirectory);

    /// <summary>백그라운드에서 아이콘을 읽어 콜백 (임의 스레드에서 호출됨).</summary>
    void Request(string path, bool isDirectory, Action<ImageSource?> callback);

    /// <summary>"텍스트 문서", "파일 폴더" 같은 형식 이름 (확장자별 캐시).</summary>
    string GetTypeName(string path, bool isDirectory);

    /// <summary>"로컬 디스크 (C:)" 같은 셸 표시 이름.</summary>
    string GetDisplayName(string path);
}

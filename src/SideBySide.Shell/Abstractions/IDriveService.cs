namespace SideBySide.Shell.Abstractions;

/// <summary>드라이브 정보와 USB 장치 안전하게 제거.</summary>
public interface IDriveService
{
    /// <summary>꺼낼 수 있는 드라이브인지: USB 메모리·광학 드라이브, 버스가 USB인 외장 디스크. 결과는 캐시된다 (느릴 수 있으니 백그라운드에서 호출).</summary>
    bool IsEjectable(string root);

    /// <summary>
    /// 탐색기의 "꺼내기"와 같은 방식으로 장치를 꺼낸다.
    /// 반환: 드라이브가 실제로 사라졌으면(안전하게 제거 가능) true, 다른 프로그램이 사용 중이면 false.
    /// </summary>
    Task<bool> EjectAsync(string root);
}

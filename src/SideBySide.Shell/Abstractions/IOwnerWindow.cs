namespace SideBySide.Shell.Abstractions;

/// <summary>셸 대화상자(진행률, 덮어쓰기 확인, 컨텍스트 메뉴)의 소유 창 핸들.</summary>
public interface IOwnerWindow
{
    IntPtr Handle { get; }
}

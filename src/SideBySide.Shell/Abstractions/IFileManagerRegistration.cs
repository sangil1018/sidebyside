namespace SideBySide.Shell.Abstractions;

/// <summary>폴더를 여는 기본 프로그램을 이 앱으로 바꾸거나 되돌린다 (현재 사용자 범위, 관리자 권한 불필요).</summary>
public interface IFileManagerRegistration
{
    /// <summary>이 앱이 기본 파일 관리자로 등록되어 있는지.</summary>
    bool IsEnabled { get; }

    /// <summary>폴더 열기 명령을 이 앱으로 바꾼다. 이전 설정은 기억해 두었다가 <see cref="Disable"/>에서 복구한다.</summary>
    void Enable(string executablePath);

    /// <summary>등록을 지우고 원래 설정(또는 설정 없음)으로 되돌린다.</summary>
    void Disable();
}

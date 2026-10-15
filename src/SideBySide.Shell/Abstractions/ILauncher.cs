namespace SideBySide.Shell.Abstractions;

public interface ILauncher
{
    /// <summary>기본 연결 프로그램으로 열기.</summary>
    void Open(string path);

    void OpenWith(string path);

    void ShowProperties(string path);

    /// <summary>Windows Terminal(없으면 cmd)을 해당 폴더에서 열기.</summary>
    void OpenTerminal(string directory);

    /// <summary>휴지통·제어판 같은 셸 위치(<c>shell:...</c>)는 탐색기로, <c>ms-settings:</c>는 Windows 설정으로 연다.</summary>
    void OpenShellPlace(string target);

    /// <summary>Windows 탐색기에서 항목 선택된 상태로 열기.</summary>
    void RevealInExplorer(string path);
}

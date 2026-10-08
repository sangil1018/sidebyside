namespace SideBySide.Shell.Abstractions;

public interface ILauncher
{
    /// <summary>기본 연결 프로그램으로 열기.</summary>
    void Open(string path);

    void OpenWith(string path);

    void ShowProperties(string path);

    /// <summary>Windows Terminal(없으면 cmd)을 해당 폴더에서 열기.</summary>
    void OpenTerminal(string directory);

    /// <summary>Windows 탐색기에서 항목 선택된 상태로 열기.</summary>
    void RevealInExplorer(string path);
}

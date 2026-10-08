namespace SideBySide.Shell.Abstractions;

/// <summary>
/// 파일 작업. Windows 셸 엔진을 사용하므로 진행률 창, 덮어쓰기 확인, 실행 취소(Ctrl+Z in 탐색기),
/// 휴지통이 탐색기와 동일하게 동작한다. 반환값: 사용자가 취소하지 않고 완료했으면 true.
/// </summary>
public interface IFileOperations
{
    Task<bool> CopyAsync(IReadOnlyList<string> sources, string destinationDirectory, bool renameOnCollision = false);

    Task<bool> MoveAsync(IReadOnlyList<string> sources, string destinationDirectory);

    Task<bool> DeleteAsync(IReadOnlyList<string> paths, bool permanent);

    Task<bool> RenameAsync(string path, string newName);

    /// <summary>휴지통으로 보낸 항목을 원래 위치로 복원 (실행 취소용). 복원한 경로 목록 반환.</summary>
    Task<IReadOnlyList<string>> RestoreFromRecycleBinAsync(IReadOnlyList<string> originalPaths);

    /// <summary>"새 폴더" 생성 후 경로 반환.</summary>
    string CreateFolder(string parentDirectory);

    /// <summary>"새 텍스트 문서.txt" 생성 후 경로 반환.</summary>
    string CreateTextFile(string parentDirectory);
}

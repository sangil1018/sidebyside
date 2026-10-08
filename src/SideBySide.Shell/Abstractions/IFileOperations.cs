namespace SideBySide.Shell.Abstractions;

/// <summary>
/// 셸 엔진이 필요한 파일 작업 (휴지통, 이름 바꾸기). 셸 진행 창은 띄우지 않는다 — 진행률은 앱이 표시.
/// 복사/이동은 진행률 보고를 위해 Core의 <c>FileTransfer</c> 엔진을 쓴다.
/// 반환값: 실패·취소 없이 완료했으면 true.
/// </summary>
public interface IFileOperations
{
    /// <summary>삭제. <paramref name="permanent"/>가 false면 휴지통으로. 확인은 호출 측(앱)이 한다.</summary>
    Task<bool> DeleteAsync(IReadOnlyList<string> paths, bool permanent);

    Task<bool> RenameAsync(string path, string newName);

    /// <summary>휴지통으로 보낸 항목을 원래 위치로 복원 (실행 취소용). 복원한 경로 목록 반환.</summary>
    Task<IReadOnlyList<string>> RestoreFromRecycleBinAsync(IReadOnlyList<string> originalPaths);

    /// <summary>"새 폴더" 생성 후 경로 반환.</summary>
    string CreateFolder(string parentDirectory);

    /// <summary>"새 텍스트 문서.txt" 생성 후 경로 반환.</summary>
    string CreateTextFile(string parentDirectory);
}

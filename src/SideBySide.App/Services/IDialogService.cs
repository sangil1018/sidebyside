using SideBySide.Core.FileSystem;
using SideBySide.Core.Filtering;
using SideBySide.Core.Rename;
using SideBySide.Core.Sessions;

namespace SideBySide.App.Services;

/// <summary>뷰모델이 창을 직접 만들지 않도록 대화상자를 추상화.</summary>
public interface IDialogService
{
    void ShowError(string message);

    bool Confirm(string message, string title);

    /// <summary>예=true, 아니요=false, 취소=null.</summary>
    bool? AskYesNoCancel(string message, string title);

    string? Prompt(string title, string label, string initialValue);

    /// <summary>복사/이동 중 같은 이름 충돌. (선택, 나머지 모두에 적용 여부)</summary>
    (ConflictChoice Choice, bool ApplyToAll) ResolveConflict(TransferConflict conflict);

    /// <summary>필터 편집. 프리셋 목록은 대화상자 안에서 수정될 수 있다.</summary>
    FilterSettings? EditFilter(FilterSettings current, IList<FilterSettings> presets);

    /// <summary>다중 이름 바꾸기. 적용한 항목 목록(실행 취소용), 취소했으면 null.</summary>
    IReadOnlyList<RenamePreview>? BatchRename(IReadOnlyList<string> paths);

    /// <summary>세션 선택. 목록은 대화상자 안에서 삭제될 수 있다.</summary>
    CompareSession? PickSession(IList<CompareSession> sessions);

    /// <summary>환경설정. 확인하면 새 값, 취소하면 null.</summary>
    AppPreferences? EditPreferences(AppPreferences current);
}

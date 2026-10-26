using System.Windows;

namespace SideBySide.Modules;

/// <summary>승격(관리자) 작업 결과.</summary>
/// <param name="Cancelled">사용자가 UAC를 거부했다.</param>
/// <param name="Output">작업이 돌려준 JSON (성공·부분 실패 모두).</param>
public sealed record ElevatedResult(bool Cancelled, string? Output, string? Error)
{
    public bool Succeeded => !Cancelled && Error is null && Output is not null;
}

/// <summary>
/// 앱이 모듈 하나에 주는 기능. 모듈마다 따로 만들어져 설정 데이터는 그 모듈 것만 읽고 쓴다.
/// UI 스레드에서 호출한다 (<see cref="RunElevatedAsync"/>는 어디서든 await 가능).
/// </summary>
public interface IModuleHost
{
    /// <summary>모듈 ID (설정 키).</summary>
    string ModuleId { get; }

    /// <summary>대화상자의 소유 창: 모듈 화면이 들어 있는 창.</summary>
    Window? Owner { get; }

    /// <summary>활성 탭의 폴더 (가상 폴더면 null).</summary>
    string? ActiveFolder { get; }

    /// <summary>활성 탭에서 선택한 항목.</summary>
    IReadOnlyList<string> SelectedPaths { get; }

    /// <summary>활성 탭에서 폴더를 연다 (파일이면 그 폴더를 열고 파일을 선택).</summary>
    void NavigateTo(string path);

    /// <summary>열려 있는 탭 중 이 폴더를 보고 있는 탭을 새로 고친다.</summary>
    void RefreshFolder(string folder);

    /// <summary>상태 표시줄에 잠깐 알림.</summary>
    void ShowNotice(string text);

    void ShowError(string message);

    bool Confirm(string message, string title);

    string? PickFolder(string title, string? initialFolder);

    IReadOnlyList<string> PickFiles(string title);

    /// <summary>설정에 저장된 모듈 데이터. 없거나 형식이 맞지 않으면 새 인스턴스.</summary>
    T LoadData<T>() where T : class, new();

    /// <summary>모듈 데이터를 설정에 저장 (앱 종료 시 파일로 기록).</summary>
    void SaveData<T>(T data) where T : class;

    /// <summary>
    /// 관리자 권한으로 미리 등록된 작업 하나를 실행한다 (UAC 확인). <paramref name="operation"/>은 앱이 허용한 작업 ID만 가능하다.
    /// </summary>
    Task<ElevatedResult> RunElevatedAsync(string operation, string payloadJson, CancellationToken cancellationToken = default);

    /// <summary>모듈 화면을 앞으로 (패널이면 그 탭을 고르고, 창이면 활성화). 백그라운드에서 일이 생겼을 때.</summary>
    void BringToFront();
}

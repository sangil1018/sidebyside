namespace SideBySide.Core;

/// <summary>
/// 앱이 쓰는 파일 위치와 실행 모드. 환경 변수로 사용자의 실제 설정과 분리해 띄울 수 있다 (테스트 하네스·UI 자동화 드라이버용).
/// <list type="bullet">
/// <item><c>SIDEBYSIDE_DATA_DIR</c>: 설정·오류 로그·진단 로그를 모두 이 폴더에 둔다.</item>
/// <item><c>SIDEBYSIDE_AUTOMATION=1</c>: 전역 단축키를 등록하지 않고, 창을 화면 밖에 포커스 없이 띄운다 (사용자 작업을 방해하지 않게).</item>
/// <item><c>SIDEBYSIDE_DIAG=1</c>: UI 스레드 지연·반복 호출을 진단 로그에 기록한다.</item>
/// </list>
/// </summary>
public static class AppPaths
{
    public const string DataDirVariable = "SIDEBYSIDE_DATA_DIR";
    public const string AutomationVariable = "SIDEBYSIDE_AUTOMATION";
    public const string DiagVariable = "SIDEBYSIDE_DIAG";

    /// <summary>분리된 데이터 폴더 (환경 변수가 없으면 null = 평소 위치).</summary>
    public static string? DataDir
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(DataDirVariable);
            return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Trim());
        }
    }

    /// <summary>설정 파일. 평소에는 %APPDATA%\SideBySide\settings.json.</summary>
    public static string SettingsFile => Path.Combine(DataDir ?? Roaming, "settings.json");

    /// <summary>오류 로그. 평소에는 %LOCALAPPDATA%\SideBySide\error.log.</summary>
    public static string ErrorLog => Path.Combine(DataDir ?? Local, "error.log");

    /// <summary>진단 로그 (<see cref="IsDiagnostics"/>일 때만 쓴다).</summary>
    public static string DiagLog => Path.Combine(DataDir ?? Local, "diag.log");

    /// <summary>UI 자동화(하네스·드라이버)로 띄운 인스턴스.</summary>
    public static bool IsAutomation => IsOn(AutomationVariable);

    public static bool IsDiagnostics => IsOn(DiagVariable);

    private static string Roaming => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SideBySide");

    private static string Local => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideBySide");

    private static bool IsOn(string variable)
        => Environment.GetEnvironmentVariable(variable)?.Trim() is "1" or "true" or "True" or "TRUE";
}

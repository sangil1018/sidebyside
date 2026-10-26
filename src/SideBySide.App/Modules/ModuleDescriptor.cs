using SideBySide.Core.Settings;
using SideBySide.Modules;

namespace SideBySide.App.Modules;

/// <summary>우클릭 메뉴를 열 때의 대상.</summary>
/// <param name="Paths">선택 항목 (빈 공간 메뉴면 비어 있음).</param>
/// <param name="Folder">활성 탭 폴더 (가상 폴더면 null).</param>
public sealed record MenuContext(IReadOnlyList<string> Paths, string? Folder, bool OnBackground);

/// <summary>
/// 모듈의 정적 정보. 앱 어셈블리 안에 있는 순수 데이터라서 메뉴·설정 화면을 그릴 때 모듈 어셈블리를 불러오지 않는다.
/// 모듈 어셈블리는 <see cref="Create"/>나 <see cref="ElevatedOperations"/>를 실제로 호출할 때 처음 로드된다.
/// </summary>
public sealed record ModuleDescriptor
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required string Glyph { get; init; }

    public ModuleOpenMode DefaultOpenMode { get; init; } = ModuleOpenMode.Panel;

    /// <summary>일부 작업에 관리자 권한(UAC)이 필요하다 (설정 화면 표시용).</summary>
    public bool UsesElevation { get; init; }

    /// <summary>우클릭 메뉴 항목 글자. null이면 우클릭 메뉴에 나오지 않는다 (도구 메뉴 전용).</summary>
    public string? MenuText { get; init; }

    /// <summary>우클릭 대상에 이 모듈 항목을 보일지. 파일 시스템을 거의 건드리지 않는 빠른 검사여야 한다.</summary>
    public Func<MenuContext, bool>? AppliesTo { get; init; }

    public required Func<IModuleHost, IToolModule> Create { get; init; }

    /// <summary>승격 프로세스에서 실행할 수 있는 작업 (작업 ID → payload JSON을 받아 결과 JSON을 돌려줌). 여기 없는 ID는 거부된다.</summary>
    public IReadOnlyDictionary<string, Func<string, string>> ElevatedOperations { get; init; } = new Dictionary<string, Func<string, string>>();

    public string CommandId => "Module." + Id;
}

using System.Runtime.CompilerServices;
using SideBySide.Core.Localization;
using SideBySide.Core.Sequences;
using SideBySide.Core.Settings;
using SideBySide.Modules;

namespace SideBySide.App.Modules;

/// <summary>
/// 앱에 들어 있는 모듈 목록. 팩토리는 모두 <see cref="MethodImplOptions.NoInlining"/> 메서드라서
/// 해당 메서드가 처음 실행될 때(= 사용자가 모듈을 처음 열 때) 비로소 모듈 어셈블리가 로드된다.
/// 꺼진 모듈이나 열지 않은 모듈은 어셈블리조차 메모리에 올라오지 않는다.
/// </summary>
public static class ModuleCatalog
{
    /// <summary>시스템 이미지 코덱(WIC)으로 읽을 수 있는 확장자 (모듈과 같은 목록).</summary>
    public static IReadOnlySet<string> ImageExtensions => ImageFormats.Extensions;

    public static IReadOnlyList<ModuleDescriptor> All { get; } =
    [
        new()
        {
            Id = "resize",
            Title = L.T("이미지 크기 조정"),
            Description = L.T("여러 이미지를 한 번에 줄이거나 지정한 크기로 바꿉니다."),
            Glyph = Glyphs.Resize,
            MenuText = L.T("이미지 크기 조정…"),
            AppliesTo = c => !c.OnBackground && c.Paths.Count > 0 && c.Paths.All(p => IsImage(p) || Directory.Exists(p)),
            Create = CreateResize,
        },
        new()
        {
            Id = "seqrenumber",
            Title = L.T("시퀀스 번호 다시 매기기"),
            Description = L.T("이미지 시퀀스의 접두어와 프레임 번호를 한 번에 바꿉니다."),
            Glyph = Glyphs.Renumber,
            MenuText = L.T("시퀀스 번호 다시 매기기…"),
            AppliesTo = IsSequenceTarget,
            Create = CreateRenumber,
        },
        new()
        {
            Id = "seqviewer",
            Title = L.T("시퀀스 뷰어"),
            Description = L.T("이미지 시퀀스를 레이어로 겹쳐 재생합니다."),
            Glyph = Glyphs.SequenceViewer,
            DefaultOpenMode = ModuleOpenMode.Window,
            MenuText = L.T("시퀀스 뷰어로 열기"),
            AppliesTo = c => IsSequenceTarget(c, ImageExtensions),
            Create = CreateViewer,
        },
        new()
        {
            Id = "tempcleaner",
            Title = L.T("임시 파일 정리"),
            Description = L.T("Temp·브라우저 캐시·업데이트 캐시를 찾아 정리합니다."),
            Glyph = Glyphs.Cleaner,
            UsesElevation = true,
            Create = CreateCleaner,
            ElevatedOperations = new Dictionary<string, Func<string, string>> { ["tempcleaner.clean"] = CleanElevated },
        },
        new()
        {
            Id = "lanshare",
            Title = L.T("LAN 직접 연결 공유"),
            Description = L.T("랜선으로 직접 연결한 두 PC의 IP·방화벽·공유 폴더를 설정합니다."),
            Glyph = Glyphs.LanShare,
            UsesElevation = true,
            MenuText = L.T("LAN 공유 폴더로 지정…"),
            AppliesTo = c => !c.OnBackground && c.Paths.Count == 1 && Directory.Exists(c.Paths[0]) && Path.GetPathRoot(c.Paths[0]) is { Length: 3 },
            Create = CreateLanShare,
            ElevatedOperations = new Dictionary<string, Func<string, string>>
            {
                ["lanshare.apply"] = LanShareApplyElevated,
                ["lanshare.reset"] = LanShareResetElevated,
            },
        },
        new()
        {
            Id = "directconnect",
            Title = L.T("Direct Connect"),
            Description = L.T("같은 Wi-Fi의 휴대폰·PC와 파일을 바로 주고받습니다 (AgentZi Direct Connect 앱과 호환)."),
            Glyph = Glyphs.DirectConnect,
            MenuText = L.T("Direct Connect로 보내기…"),
            AppliesTo = c => !c.OnBackground && c.Paths.Count > 0 && c.Paths.All(File.Exists),
            Create = CreateDirectConnect,
        },
    ];

    public static ModuleDescriptor? Find(string id) => All.FirstOrDefault(m => m.Id == id);

    /// <summary>승격 작업 ID로 처리기를 찾는다 (등록된 것만).</summary>
    public static Func<string, string>? FindElevated(string operation)
    {
        foreach (var module in All)
        {
            if (module.ElevatedOperations.TryGetValue(operation, out var handler)) return handler;
        }

        return null;
    }

    public static bool IsImage(string path) => ImageFormats.IsImage(path);

    private static bool IsSequenceTarget(MenuContext c) => IsSequenceTarget(c, null);

    /// <summary>
    /// 프레임 번호 파일(하나 이상) 또는 시퀀스가 든 폴더 하나. 빈 공간 메뉴는 현재 폴더 기준.
    /// 네트워크 위치는 폴더를 읽느라 메뉴가 늦게 뜨지 않도록 검사 없이 보인다 (시퀀스가 없으면 도구가 알려 준다).
    /// </summary>
    private static bool IsSequenceTarget(MenuContext c, IReadOnlySet<string>? extensions)
    {
        if (c.OnBackground) return c.Folder is not null && HasSequence(c.Folder, extensions);
        if (c.Paths.Count == 1 && Directory.Exists(c.Paths[0])) return HasSequence(c.Paths[0], extensions);
        return c.Paths.Count > 0 && c.Paths.All(p => SequenceDetector.LooksLikeFrame(p, extensions));
    }

    private static bool HasSequence(string folder, IReadOnlySet<string>? extensions)
        => IsNetworkPath(folder) || SequenceDetector.FolderHasSequence(folder, extensions);

    private static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try
        {
            return Path.GetPathRoot(path) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // ── 지연 로드 지점: 아래 메서드만 모듈 형식을 직접 참조한다 ─────────────

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IToolModule CreateResize(IModuleHost host) => new SideBySide.Modules.Resize.ResizeModule(host);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IToolModule CreateRenumber(IModuleHost host) => new SideBySide.Modules.SeqRenumber.RenumberModule(host);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IToolModule CreateViewer(IModuleHost host) => new SideBySide.Modules.SeqViewer.ViewerModule(host);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IToolModule CreateCleaner(IModuleHost host) => new SideBySide.Modules.TempCleaner.CleanerModule(host);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string CleanElevated(string payload) => SideBySide.Modules.TempCleaner.CleanerElevated.Clean(payload);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IToolModule CreateLanShare(IModuleHost host) => new SideBySide.Modules.LanShare.LanShareModule(host);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string LanShareApplyElevated(string payload) => SideBySide.Modules.LanShare.LanShareElevated.Apply(payload);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string LanShareResetElevated(string payload) => SideBySide.Modules.LanShare.LanShareElevated.Reset(payload);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IToolModule CreateDirectConnect(IModuleHost host) => new SideBySide.Modules.DirectConnect.DirectConnectModule(host);
}

using System.Windows;

namespace SideBySide.Modules;

/// <summary>
/// 모듈 공통 구현: 화면을 처음 접근할 때 한 번 만들고, 백그라운드 서비스가 없으면 화면을 닫을 때 해제된다.
/// </summary>
public abstract class ToolModuleBase(IModuleHost host) : IToolModule
{
    private FrameworkElement? _view;

    protected IModuleHost Host { get; } = host;

    protected bool IsDisposed { get; private set; }

    public FrameworkElement View => _view ??= CreateView();

    public virtual bool KeepsRunningWhenClosed => false;

    public abstract void Open(ModuleInvocation invocation);

    public virtual void OnClosed()
    {
    }

    public virtual IReadOnlyList<ModuleMenuItem> GetDynamicMenuItems(IReadOnlyList<string> paths) => [];

    public virtual bool CanClose() => true;

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected abstract FrameworkElement CreateView();

    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>작업 중이면 취소할지 묻는다 (공통 문구).</summary>
    protected bool ConfirmCancelRunning(bool running, string title)
        => !running || Host.Confirm(Core.Localization.L.T("작업이 진행 중입니다. 취소하고 닫을까요?"), title);
}

/// <summary>Windows 이미지 코덱(WIC)으로 읽을 수 있는 확장자 (점 없이). 앱 메뉴 판단과 모듈이 같은 목록을 쓴다.</summary>
public static class ImageFormats
{
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "jpe", "png", "bmp", "gif", "tif", "tiff", "webp", "jxr", "wdp", "ico", "heic", "heif", "avif",
    };

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path).TrimStart('.'));
}

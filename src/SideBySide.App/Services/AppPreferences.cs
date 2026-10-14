using SideBySide.Core.Settings;

namespace SideBySide.App.Services;

/// <summary>환경설정 창에서 편집하는 값.</summary>
public sealed record AppPreferences(ThemeMode Theme, bool DefaultFileManager, OpenShortcut OpenShortcut);

using System.Text;
using System.Text.RegularExpressions;
using SideBySide.Core.FileSystem;

namespace SideBySide.Core.Filtering;

/// <summary>
/// <see cref="FilterSettings"/>를 컴파일한 불변 매처. 스레드 안전하므로 병렬 비교에서 공유한다.
/// </summary>
public sealed class FilterSet
{
    private static readonly char[] Separators = [';', '\n', '\r'];

    private readonly Regex? _excludeAny;
    private readonly Regex? _excludeDirs;
    private readonly Regex? _includeFiles;

    private FilterSet(FilterSettings settings, bool showHidden)
    {
        Settings = settings;
        ShowHidden = showHidden;

        var exclude = SplitPatterns(settings.Exclude);
        _excludeAny = Build(exclude.Where(p => !IsDirPattern(p)));
        _excludeDirs = Build(exclude.Where(IsDirPattern).Select(p => p.TrimEnd('\\', '/')));
        _includeFiles = Build(SplitPatterns(settings.Include).Where(p => !IsDirPattern(p)));
    }

    public FilterSettings Settings { get; }

    public bool ShowHidden { get; }

    /// <summary>사용자 패턴이 하나라도 있는지 (숨김 파일 설정은 제외).</summary>
    public bool HasPatterns => _excludeAny is not null || _excludeDirs is not null || _includeFiles is not null;

    public static FilterSet Create(FilterSettings? settings, bool showHidden)
        => new(settings ?? FilterSettings.None, showHidden);

    public bool IsVisible(FileEntry entry) => IsVisible(entry.Name, entry.IsDirectory, entry.Attributes);

    public bool IsVisible(string name, bool isDirectory, FileAttributes attributes)
    {
        if (!ShowHidden && (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
            return false;

        if (_excludeAny?.IsMatch(name) == true) return false;
        if (isDirectory && _excludeDirs?.IsMatch(name) == true) return false;
        if (!isDirectory && _includeFiles is not null && !_includeFiles.IsMatch(name)) return false;
        return true;
    }

    public static IReadOnlyList<string> SplitPatterns(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsDirPattern(string pattern) => pattern.EndsWith('\\') || pattern.EndsWith('/');

    private static Regex? Build(IEnumerable<string> patterns)
    {
        var sb = new StringBuilder();
        foreach (var p in patterns)
        {
            if (p.Length == 0) continue;
            sb.Append(sb.Length == 0 ? "^(?:" : "|");
            sb.Append(WildcardToRegex(p));
        }

        if (sb.Length == 0) return null;
        sb.Append(")$");
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    internal static string WildcardToRegex(string pattern)
        => Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal);
}

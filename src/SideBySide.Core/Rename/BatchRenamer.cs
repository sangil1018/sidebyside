using System.Text.RegularExpressions;
using SideBySide.Core.FileSystem;
using SideBySide.Core.Localization;

namespace SideBySide.Core.Rename;

/// <summary>
/// 다중 이름 바꾸기 옵션.
/// 템플릿 토큰: <c>{name}</c> 확장자 뺀 이름(찾기/바꾸기 적용 후), <c>{ext}</c> 점 포함 확장자, <c>{n}</c> 번호, <c>{n:3}</c> 3자리 0 채움.
/// </summary>
public sealed record RenameOptions
{
    public string Find { get; init; } = string.Empty;

    public string Replace { get; init; } = string.Empty;

    public bool UseRegex { get; init; }

    public bool MatchCase { get; init; }

    public string Template { get; init; } = "{name}{ext}";

    public int StartNumber { get; init; } = 1;

    public int Step { get; init; } = 1;
}

public sealed record RenamePreview(string OriginalPath, string NewName, string? Error)
{
    public string OriginalName => Path.GetFileName(OriginalPath);

    public bool IsChanged => Error is null && !string.Equals(OriginalName, NewName, StringComparison.Ordinal);

    public string NewPath => Path.Combine(Path.GetDirectoryName(OriginalPath)!, NewName);
}

public sealed class BatchRenameException(string message, Exception inner) : Exception(message, inner);

public static partial class BatchRenamer
{
    public static IReadOnlyList<RenamePreview> Preview(IReadOnlyList<string> paths, RenameOptions options)
    {
        Regex? regex = null;
        string? regexError = null;
        if (options.UseRegex && options.Find.Length > 0)
        {
            try { regex = new Regex(options.Find, options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase); }
            catch (ArgumentException ex) { regexError = L.T("정규식 오류: ") + ex.Message; }
        }

        var result = new List<RenamePreview>(paths.Count);
        var number = options.StartNumber;
        foreach (var path in paths)
        {
            if (regexError is not null)
            {
                result.Add(new RenamePreview(path, Path.GetFileName(path), regexError));
                continue;
            }

            var isDir = Directory.Exists(path);
            var fileName = Path.GetFileName(path);
            var ext = isDir ? string.Empty : Path.GetExtension(fileName);
            var name = isDir ? fileName : Path.GetFileNameWithoutExtension(fileName);

            if (options.Find.Length > 0)
            {
                name = regex is not null
                    ? regex.Replace(name, options.Replace)
                    : name.Replace(options.Find, options.Replace, options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            }

            var n = number;
            var newName = TokenRegex().Replace(options.Template, m => m.Groups["tok"].Value switch
            {
                "name" => name,
                "ext" => ext,
                _ => m.Groups["pad"].Success ? n.ToString().PadLeft(int.Parse(m.Groups["pad"].Value), '0') : n.ToString(),
            });
            number += options.Step;

            result.Add(new RenamePreview(path, newName, PathNames.Validate(newName)));
        }

        return MarkConflicts(result);
    }

    /// <summary>
    /// 두 단계(임시 이름 → 최종 이름)로 적용해 서로 이름을 맞바꾸는 경우나 대소문자만 바꾸는 경우도 처리한다.
    /// 중간에 실패하면 이미 바꾼 항목을 원래대로 되돌린다.
    /// </summary>
    public static void Apply(IReadOnlyList<RenamePreview> items)
    {
        var todo = items.Where(i => i.IsChanged).ToList();
        if (todo.Count == 0) return;
        if (items.Any(i => i.Error is not null))
            throw new InvalidOperationException(L.T("오류가 있는 항목이 있어 적용할 수 없습니다."));

        var done = new List<(string From, string To)>();
        try
        {
            var temps = new List<(RenamePreview Item, string Temp)>();
            foreach (var item in todo)
            {
                var temp = Path.Combine(Path.GetDirectoryName(item.OriginalPath)!, $".sbs-rename-{Guid.NewGuid():N}");
                Move(item.OriginalPath, temp);
                done.Add((item.OriginalPath, temp));
                temps.Add((item, temp));
            }

            foreach (var (item, temp) in temps)
            {
                if (File.Exists(item.NewPath) || Directory.Exists(item.NewPath))
                    throw new IOException(L.F("'{0}' 이(가) 이미 있습니다.", item.NewName));
                Move(temp, item.NewPath);
                done.Add((temp, item.NewPath));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            for (int i = done.Count - 1; i >= 0; i--)
            {
                try { Move(done[i].To, done[i].From); }
                catch (Exception rollbackEx) when (rollbackEx is IOException or UnauthorizedAccessException) { /* 최선을 다해 복구 */ }
            }

            throw new BatchRenameException(L.T("이름 바꾸기에 실패하여 원래대로 되돌렸습니다: ") + ex.Message, ex);
        }
    }

    private static List<RenamePreview> MarkConflicts(List<RenamePreview> items)
    {
        var sources = new HashSet<string>(items.Select(i => i.OriginalPath), StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(i => i.Error is null))
            seen[item.NewPath] = seen.GetValueOrDefault(item.NewPath) + 1;

        return items.Select(item =>
        {
            if (item.Error is not null) return item;
            if (seen[item.NewPath] > 1) return item with { Error = L.T("다른 항목과 이름이 겹칩니다.") };
            if (item.IsChanged && !sources.Contains(item.NewPath)
                && !string.Equals(item.NewPath, item.OriginalPath, StringComparison.OrdinalIgnoreCase)
                && (File.Exists(item.NewPath) || Directory.Exists(item.NewPath)))
                return item with { Error = L.T("같은 이름의 항목이 이미 있습니다.") };
            return item;
        }).ToList();
    }

    private static void Move(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    [GeneratedRegex(@"\{(?<tok>name|ext|n)(?::(?<pad>\d{1,2}))?\}")]
    private static partial Regex TokenRegex();
}

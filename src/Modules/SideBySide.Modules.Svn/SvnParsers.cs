using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace SideBySide.Modules.Svn;

public enum SvnChangeKind
{
    Modified,
    Added,
    Deleted,

    /// <summary>svn을 거치지 않고 디스크에서 지운 파일 (커밋하면 svn에서도 지운다).</summary>
    Missing,

    /// <summary>버전 관리 밖의 새 파일 (커밋하면 svn에 추가한다).</summary>
    Unversioned,
    Conflicted,
}

/// <summary>작업 사본의 변경 하나.</summary>
/// <param name="Path">작업 사본 최상위 기준 경로 ('/' 구분).</param>
public sealed record SvnChange(string Path, SvnChangeKind Kind);

/// <summary><c>svn status</c> 결과. <see cref="Incoming"/>은 서버까지 확인했을 때만 (그 외에는 null).</summary>
internal sealed record SvnStatus(IReadOnlyList<SvnChange> Changes, int? Incoming)
{
    public static SvnStatus Empty { get; } = new([], null);
}

/// <summary><c>svn info</c>에서 쓰는 값.</summary>
/// <param name="RelativeUrl">저장소 최상위 기준 주소 (예: <c>^/project/trunk</c>).</param>
internal sealed record SvnInfo(string Url, string RelativeUrl, string RepositoryRoot, long Revision);

/// <summary>
/// 표준 배치(trunk / branches / tags)에서 지금 작업 사본이 가리키는 위치.
/// </summary>
/// <param name="Base">trunk·branches·tags가 들어 있는 폴더의 저장소 경로 (예: <c>/project</c>, 저장소 최상위면 빈 문자열).</param>
/// <param name="Branch">지금 브랜치의 저장소 경로 (<c>/project/trunk</c>, <c>/project/branches/x</c>).</param>
/// <param name="Name">화면에 보일 이름 (trunk, x, tags/v1).</param>
/// <param name="SubPath">브랜치 안에서 작업 사본이 가리키는 하위 경로 (예: <c>/src</c>, 브랜치 전체면 빈 문자열).</param>
internal sealed record SvnLayout(string Base, string Branch, string Name, string SubPath);

public sealed record SvnBranch(string Name, string Path, bool IsCurrent);

/// <summary>리비전 하나에서 바뀐 경로.</summary>
/// <param name="RepositoryPath">저장소 경로 (<c>/trunk/src/a.cs</c>).</param>
/// <param name="IsDirectory">폴더 자체의 추가·삭제·속성 변경.</param>
public sealed record SvnLogPath(string RepositoryPath, char Action, bool IsDirectory);

public sealed record SvnRevision(long Revision, string Author, DateTimeOffset Date, string Message, IReadOnlyList<SvnLogPath> Paths)
{
    public string Subject
    {
        get
        {
            var text = Message.TrimStart();
            var newline = text.IndexOfAny(['\r', '\n']);
            return (newline >= 0 ? text[..newline] : text).Trim();
        }
    }

    public string Body
    {
        get
        {
            var text = Message.TrimStart();
            var newline = text.IndexOfAny(['\r', '\n']);
            return newline >= 0 ? text[newline..].Trim() : string.Empty;
        }
    }
}

public enum DiffLineKind
{
    /// <summary>파일 단위 안내 (이진 파일, 속성 변경 등).</summary>
    Note,
    Hunk,
    Context,
    Added,
    Removed,
}

public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldNumber = null, int? NewNumber = null)
{
    public string OldNumberText => OldNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public string NewNumberText => NewNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>svn 출력 해석 (순수 함수 — 프로세스·파일을 건드리지 않는다).</summary>
internal static class SvnParsers
{
    /// <summary>diff 화면에 보일 최대 줄 수 (그 이상은 잘라 내고 안내 줄을 붙인다).</summary>
    public const int MaxDiffLines = 5000;

    /// <summary><c>svn status --xml</c> (서버 확인 <c>-u</c> 포함 가능) 출력.</summary>
    public static SvnStatus ParseStatus(string xml)
    {
        var changes = new List<SvnChange>();
        var incoming = 0;
        var checkedServer = false;

        var doc = Load(xml);
        foreach (var target in doc.Root?.Elements("target") ?? [])
        {
            if (target.Element("against") is not null) checkedServer = true;

            foreach (var entry in target.Elements("entry"))
            {
                var path = Normalize((string?)entry.Attribute("path") ?? string.Empty);
                if (path.Length == 0 || path == ".") path = string.Empty;

                var repos = entry.Element("repos-status");
                if (repos is not null)
                {
                    var reposItem = (string?)repos.Attribute("item") ?? "none";
                    var reposProps = (string?)repos.Attribute("props") ?? "none";
                    if ((reposItem != "none" && reposItem != "normal") || reposProps == "modified") incoming++;
                }

                if (path.Length == 0 || entry.Element("wc-status") is not { } wc) continue;

                var item = (string?)wc.Attribute("item") ?? "normal";
                var props = (string?)wc.Attribute("props") ?? "none";
                var treeConflict = (string?)wc.Attribute("tree-conflicted") == "true";

                SvnChangeKind? kind = treeConflict || props == "conflicted" ? (SvnChangeKind?)SvnChangeKind.Conflicted : item switch
                {
                    "modified" or "replaced" or "merged" => SvnChangeKind.Modified,
                    "added" => SvnChangeKind.Added,
                    "deleted" => SvnChangeKind.Deleted,
                    "missing" => SvnChangeKind.Missing,
                    "unversioned" => SvnChangeKind.Unversioned,
                    "conflicted" or "obstructed" or "incomplete" => SvnChangeKind.Conflicted,
                    "normal" when props == "modified" => SvnChangeKind.Modified,
                    _ => null, // normal, ignored, external, none
                };
                if (kind is { } k) changes.Add(new SvnChange(path, k));
            }
        }

        return new SvnStatus(changes, checkedServer ? incoming : null);
    }

    /// <summary><c>svn info --xml</c> 출력 (첫 항목).</summary>
    public static SvnInfo ParseInfo(string xml)
    {
        var entry = Load(xml).Root?.Element("entry") ?? throw new FormatException("svn info: no entry");
        var url = entry.Element("url")?.Value ?? string.Empty;
        var root = entry.Element("repository")?.Element("root")?.Value ?? string.Empty;
        var relative = entry.Element("relative-url")?.Value;
        if (string.IsNullOrEmpty(relative))
        {
            // svn 1.7 이하: relative-url이 없다
            relative = root.Length > 0 && url.StartsWith(root, StringComparison.Ordinal) ? "^" + url[root.Length..] : url;
            if (relative == "^") relative = "^/";
        }

        _ = long.TryParse((string?)entry.Attribute("revision"), NumberStyles.None, CultureInfo.InvariantCulture, out var revision);
        return new SvnInfo(url, Uri.UnescapeDataString(relative), root, revision);
    }

    /// <summary>
    /// 저장소 경로에서 trunk / branches/x / tags/x 위치를 찾는다. 표준 배치가 아니면 null.
    /// </summary>
    /// <param name="relativeUrl"><c>^/project/branches/x/src</c> 같은 주소 (앞의 ^는 있어도 없어도 된다).</param>
    public static SvnLayout? ParseLayout(string relativeUrl)
    {
        var path = relativeUrl.StartsWith('^') ? relativeUrl[1..] : relativeUrl;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            var basePath = i == 0 ? string.Empty : "/" + string.Join('/', parts[..i]);
            if (parts[i] == "trunk")
                return new SvnLayout(basePath, basePath + "/trunk", "trunk", Tail(parts, i + 1));
            if ((parts[i] == "branches" || parts[i] == "tags") && i + 1 < parts.Length)
            {
                var name = parts[i] == "tags" ? "tags/" + parts[i + 1] : parts[i + 1];
                return new SvnLayout(basePath, basePath + "/" + parts[i] + "/" + parts[i + 1], name, Tail(parts, i + 2));
            }
        }

        return null;
    }

    /// <summary><c>svn list</c> 출력 (폴더는 끝에 '/'): 폴더 이름만.</summary>
    public static IReadOnlyList<string> ParseList(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.EndsWith('/') && l.Length > 1)
            .Select(l => l[..^1])
            .ToList();

    /// <summary>표준 배치의 브랜치 목록: trunk, branches/*. 지금 브랜치가 tags 아래면 그것도 넣는다.</summary>
    public static IReadOnlyList<SvnBranch> BuildBranches(SvnLayout layout, IReadOnlyList<string> branchNames)
    {
        var list = new List<SvnBranch> { new("trunk", layout.Base + "/trunk", layout.Name == "trunk") };
        foreach (var name in branchNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var path = layout.Base + "/branches/" + name;
            list.Add(new SvnBranch(name, path, layout.Branch == path));
        }

        if (!list.Any(b => b.IsCurrent)) list.Add(new SvnBranch(layout.Name, layout.Branch, true));
        return list;
    }

    /// <summary><c>svn log --xml</c> (<c>-v</c> 포함 가능) 출력.</summary>
    public static IReadOnlyList<SvnRevision> ParseLog(string xml)
    {
        var revisions = new List<SvnRevision>();
        foreach (var entry in Load(xml).Root?.Elements("logentry") ?? [])
        {
            if (!long.TryParse((string?)entry.Attribute("revision"), NumberStyles.None, CultureInfo.InvariantCulture, out var revision)) continue;
            _ = DateTimeOffset.TryParse(entry.Element("date")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date);
            var paths = entry.Element("paths")?.Elements("path")
                .Select(p => new SvnLogPath(p.Value, ((string?)p.Attribute("action") ?? "M").FirstOrDefault('M'), (string?)p.Attribute("kind") == "dir"))
                .ToList() ?? [];
            revisions.Add(new SvnRevision(revision, entry.Element("author")?.Value ?? string.Empty, date,
                entry.Element("msg")?.Value ?? string.Empty, paths));
        }

        return revisions;
    }

    /// <summary>
    /// 저장소 경로를 화면용 경로로: 작업 사본이 가리키는 위치(<paramref name="checkoutPath"/>) 아래면 그 기준 상대 경로, 아니면 그대로.
    /// </summary>
    public static string DisplayPath(string repositoryPath, string checkoutPath)
    {
        var prefix = checkoutPath.TrimEnd('/') + "/";
        return prefix.Length > 1 && repositoryPath.StartsWith(prefix, StringComparison.Ordinal) ? repositoryPath[prefix.Length..] : repositoryPath;
    }

    /// <summary>
    /// unified diff → 화면 줄. 파일 머리글(Index:, =====, ---, +++)은 버리고 의미 있는 안내만 남긴다.
    /// </summary>
    public static IReadOnlyList<DiffLine> ParseDiff(string output)
    {
        var lines = new List<DiffLine>();
        var (oldNumber, newNumber) = (0, 0);
        var inHunk = false;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (lines.Count >= MaxDiffLines)
            {
                lines.Add(new DiffLine(DiffLineKind.Note, "…"));
                break;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = TryParseHunk(line, out oldNumber, out newNumber);
                lines.Add(new DiffLine(DiffLineKind.Hunk, line));
                continue;
            }

            if (line.StartsWith("Index: ", StringComparison.Ordinal)
                || line.StartsWith("Property changes on: ", StringComparison.Ordinal)
                || (line.Length >= 20 && line.All(c => c is '=' or '_')))
            {
                inHunk = false;
                if (line.StartsWith("Property changes on: ", StringComparison.Ordinal)) lines.Add(new DiffLine(DiffLineKind.Note, line));
                continue;
            }

            if (!inHunk)
            {
                if (line.Length == 0
                    || line.StartsWith("--- ", StringComparison.Ordinal)
                    || line.StartsWith("+++ ", StringComparison.Ordinal))
                    continue;
                lines.Add(new DiffLine(DiffLineKind.Note, line.Trim()));
                continue;
            }

            if (line.Length == 0) continue;

            switch (line[0])
            {
                case '+':
                    lines.Add(new DiffLine(DiffLineKind.Added, line[1..], null, newNumber++));
                    break;
                case '-':
                    lines.Add(new DiffLine(DiffLineKind.Removed, line[1..], oldNumber++, null));
                    break;
                case ' ':
                    lines.Add(new DiffLine(DiffLineKind.Context, line[1..], oldNumber++, newNumber++));
                    break;
                default:
                    lines.Add(new DiffLine(DiffLineKind.Note, line.TrimStart('\\', ' ')));
                    break;
            }
        }

        return lines;
    }

    /// <summary>파일 전체를 한쪽 줄로 (새 파일 = 모두 추가, 지운 파일 = 모두 삭제).</summary>
    public static IReadOnlyList<DiffLine> WholeFile(string text, bool added)
    {
        var lines = new List<DiffLine>();
        var split = text.Split('\n');
        var count = split.Length > 0 && split[^1].Length == 0 ? split.Length - 1 : split.Length;
        for (int i = 0; i < count; i++)
        {
            if (lines.Count >= MaxDiffLines)
            {
                lines.Add(new DiffLine(DiffLineKind.Note, "…"));
                break;
            }

            var line = split[i].EndsWith('\r') ? split[i][..^1] : split[i];
            lines.Add(added ? new DiffLine(DiffLineKind.Added, line, null, i + 1) : new DiffLine(DiffLineKind.Removed, line, i + 1, null));
        }

        return lines;
    }

    private static string Tail(string[] parts, int start) => start >= parts.Length ? string.Empty : "/" + string.Join('/', parts[start..]);

    /// <summary>svn이 내는 Windows 경로("src\a.cs", "./a.cs")를 '/' 구분 상대 경로로.</summary>
    private static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        return p.StartsWith("./", StringComparison.Ordinal) ? p[2..] : p;
    }

    private static XDocument Load(string xml)
    {
        try
        {
            return XDocument.Parse(xml.Length == 0 ? "<empty />" : xml);
        }
        catch (XmlException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
    }

    /// <summary>"@@ -12,7 +12,9 @@"에서 시작 줄 번호를 읽는다.</summary>
    private static bool TryParseHunk(string line, out int oldStart, out int newStart)
    {
        (oldStart, newStart) = (0, 0);
        var foundOld = false;
        var foundNew = false;
        foreach (var part in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length < 2) continue;
            var comma = part.IndexOf(',');
            var number = comma > 0 ? part.AsSpan(1, comma - 1) : part.AsSpan(1);
            if (part[0] == '-' && !foundOld)
                foundOld = int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out oldStart);
            else if (part[0] == '+' && !foundNew)
                foundNew = int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out newStart);
        }

        return foundOld && foundNew;
    }
}

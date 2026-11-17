using System.Globalization;

namespace SideBySide.Modules.Git;

public enum GitChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Untracked,
    Conflicted,
}

/// <summary>작업 트리의 변경 하나 (HEAD 기준, 스테이지 여부는 구분하지 않는다).</summary>
/// <param name="Path">저장소 루트 기준 경로 ('/' 구분).</param>
/// <param name="OldPath">이름이 바뀐 경우의 이전 경로.</param>
public sealed record GitChange(string Path, string? OldPath, GitChangeKind Kind);

internal sealed record GitStatus(string? Branch, bool Detached, string? Upstream, int Ahead, int Behind, IReadOnlyList<GitChange> Changes)
{
    public static GitStatus Empty { get; } = new(null, false, null, 0, 0, []);
}

public sealed record GitBranch(string Name, bool IsRemote, bool IsCurrent);

public sealed record GitCommit(string Hash, string ShortHash, string Author, DateTimeOffset Date, string Subject, string Body);

public enum DiffLineKind
{
    /// <summary>파일 단위 안내 (새 파일, 이진 파일, 이름 변경 등).</summary>
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

/// <summary>git 출력 해석 (순수 함수 — 프로세스·파일을 건드리지 않는다).</summary>
internal static class GitParsers
{
    /// <summary>log 레코드 구분자 (0x1E)와 필드 구분자 (0x1F).</summary>
    public const char RecordSeparator = '\u001e';

    public const char FieldSeparator = '\u001f';

    /// <summary><c>git log</c>에 넘길 형식. <see cref="ParseLog"/>와 짝.</summary>
    public const string LogFormat = "--format=%H%x1f%h%x1f%an%x1f%aI%x1f%s%x1f%b%x1e";

    /// <summary>diff 화면에 보일 최대 줄 수 (그 이상은 잘라 내고 안내 줄을 붙인다).</summary>
    public const int MaxDiffLines = 5000;

    /// <summary><c>git status --porcelain=v2 --branch -z</c> 출력.</summary>
    public static GitStatus ParseStatus(string output)
    {
        string? branch = null;
        string? upstream = null;
        var detached = false;
        var (ahead, behind) = (0, 0);
        var changes = new List<GitChange>();

        var tokens = output.Split('\0');
        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length < 2) continue;

            if (token.StartsWith("# ", StringComparison.Ordinal))
            {
                if (token.StartsWith("# branch.head ", StringComparison.Ordinal))
                {
                    var name = token["# branch.head ".Length..];
                    detached = name == "(detached)";
                    branch = detached ? null : name;
                }
                else if (token.StartsWith("# branch.upstream ", StringComparison.Ordinal))
                {
                    upstream = token["# branch.upstream ".Length..];
                }
                else if (token.StartsWith("# branch.ab ", StringComparison.Ordinal))
                {
                    foreach (var part in token["# branch.ab ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (part.Length < 2 || !int.TryParse(part.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var count)) continue;
                        if (part[0] == '+') ahead = count;
                        else if (part[0] == '-') behind = count;
                    }
                }

                continue;
            }

            switch (token[0])
            {
                case '1':
                {
                    // 1 XY sub mH mI mW hH hI path
                    var parts = token.Split(' ', 9);
                    if (parts.Length == 9) changes.Add(new GitChange(parts[8], null, KindOf(parts[1])));
                    break;
                }

                case '2':
                {
                    // 2 XY sub mH mI mW hH hI Xscore path <NUL> origPath
                    var parts = token.Split(' ', 10);
                    var original = i + 1 < tokens.Length ? tokens[++i] : null;
                    if (parts.Length == 10)
                    {
                        // 복사(C)는 원본이 그대로 남으므로 새 파일로 다룬다
                        var copied = parts[8].StartsWith('C');
                        changes.Add(copied
                            ? new GitChange(parts[9], null, GitChangeKind.Added)
                            : new GitChange(parts[9], original, GitChangeKind.Renamed));
                    }

                    break;
                }

                case 'u':
                {
                    // u XY sub m1 m2 m3 mW h1 h2 h3 path
                    var parts = token.Split(' ', 11);
                    if (parts.Length == 11) changes.Add(new GitChange(parts[10], null, GitChangeKind.Conflicted));
                    break;
                }

                case '?':
                    changes.Add(new GitChange(token[2..], null, GitChangeKind.Untracked));
                    break;
            }
        }

        return new GitStatus(branch, detached, upstream, ahead, behind, changes);
    }

    /// <summary><see cref="LogFormat"/>으로 받은 <c>git log</c> 출력.</summary>
    public static IReadOnlyList<GitCommit> ParseLog(string output)
    {
        var commits = new List<GitCommit>();
        foreach (var record in output.Split(RecordSeparator))
        {
            var fields = record.TrimStart('\r', '\n').Split(FieldSeparator);
            if (fields.Length < 6 || fields[0].Length == 0) continue;
            _ = DateTimeOffset.TryParse(fields[3], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
            commits.Add(new GitCommit(fields[0], fields[1], fields[2], date, fields[4], fields[5].Trim()));
        }

        return commits;
    }

    /// <summary>
    /// <c>git branch -a --format=%(HEAD)%09%(refname)</c> 출력. 지역 브랜치가 있는 원격 브랜치와 <c>origin/HEAD</c>는 뺀다.
    /// </summary>
    public static IReadOnlyList<GitBranch> ParseBranches(string output)
    {
        var local = new List<GitBranch>();
        var remote = new List<GitBranch>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var current = line[..tab].Trim() == "*";
            var reference = line[(tab + 1)..].Trim();
            if (reference.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                local.Add(new GitBranch(reference["refs/heads/".Length..], false, current));
            }
            else if (reference.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                var name = reference["refs/remotes/".Length..];
                if (!name.EndsWith("/HEAD", StringComparison.Ordinal)) remote.Add(new GitBranch(name, true, false));
            }
        }

        var localNames = local.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var branch in remote)
        {
            var slash = branch.Name.IndexOf('/');
            var shortName = slash >= 0 ? branch.Name[(slash + 1)..] : branch.Name;
            if (!localNames.Contains(shortName)) local.Add(branch);
        }

        return local;
    }

    /// <summary><c>git show --format= --name-status -z</c> 출력 (커밋 하나의 변경 파일).</summary>
    public static IReadOnlyList<GitChange> ParseNameStatus(string output)
    {
        var changes = new List<GitChange>();
        var tokens = output.Split('\0');
        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            var status = tokens[i].Trim('\r', '\n');
            if (status.Length == 0) continue;
            var path = tokens[++i];
            switch (status[0])
            {
                case 'R' when i + 1 < tokens.Length:
                    changes.Add(new GitChange(tokens[++i], path, GitChangeKind.Renamed));
                    break;
                case 'C' when i + 1 < tokens.Length:
                    changes.Add(new GitChange(tokens[++i], null, GitChangeKind.Added));
                    break;
                case 'A':
                    changes.Add(new GitChange(path, null, GitChangeKind.Added));
                    break;
                case 'D':
                    changes.Add(new GitChange(path, null, GitChangeKind.Deleted));
                    break;
                case 'U':
                    changes.Add(new GitChange(path, null, GitChangeKind.Conflicted));
                    break;
                default:
                    changes.Add(new GitChange(path, null, GitChangeKind.Modified));
                    break;
            }
        }

        return changes;
    }

    /// <summary>unified diff → 화면 줄. 파일 머리글(diff --git, index, ---, +++)은 버리고 의미 있는 안내만 남긴다.</summary>
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

            if (line.StartsWith("diff --git ", StringComparison.Ordinal) || line.StartsWith("diff --cc ", StringComparison.Ordinal))
            {
                inHunk = false;
                continue;
            }

            if (!inHunk)
            {
                if (line.Length == 0
                    || line.StartsWith("index ", StringComparison.Ordinal)
                    || line.StartsWith("--- ", StringComparison.Ordinal)
                    || line.StartsWith("+++ ", StringComparison.Ordinal))
                    continue;
                lines.Add(new DiffLine(DiffLineKind.Note, line));
                continue;
            }

            if (line.Length == 0)
            {
                // 출력 끝의 빈 조각 또는 공백이 잘린 빈 문맥 줄
                continue;
            }

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
                    // "\ No newline at end of file" 등
                    lines.Add(new DiffLine(DiffLineKind.Note, line.TrimStart('\\', ' ')));
                    break;
            }
        }

        return lines;
    }

    private static GitChangeKind KindOf(string xy)
    {
        if (xy.Length < 2) return GitChangeKind.Modified;
        // 작업 트리 상태(Y)가 있으면 그것을, 없으면 스테이지 상태(X)를 따른다. 단 스테이지에 새로 추가된 파일은 계속 "추가".
        var (x, y) = (xy[0], xy[1]);
        if (y == 'D' || (y == '.' && x == 'D')) return GitChangeKind.Deleted;
        if (x == 'A') return GitChangeKind.Added;
        if (x == 'R' || y == 'R') return GitChangeKind.Renamed;
        return GitChangeKind.Modified;
    }

    /// <summary>"@@ -12,7 +12,9 @@" (병합 diff의 "@@@ -a -b +c @@@" 포함)에서 시작 줄 번호를 읽는다.</summary>
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

namespace SideBySide.Core.Sequences;

/// <summary>번호 다시 매기기 옵션.</summary>
/// <param name="NewPrefix">새 접두어. null이면 그대로.</param>
/// <param name="Renumber">true면 <paramref name="StartFrame"/>부터 1씩 연속 번호, false면 원래 번호 유지.</param>
/// <param name="Padding">자릿수. 0 이하면 원래 자릿수.</param>
/// <param name="NewSeparator">접두어와 번호 사이 구분자. null이면 그대로.</param>
public sealed record RenumberOptions(string? NewPrefix, bool Renumber, int StartFrame, int Padding, string? NewSeparator = null);

public enum RenumberMode
{
    /// <summary>원본 폴더 옆 <c>&lt;폴더&gt;_renamed</c>에 복사.</summary>
    Copy,

    /// <summary>원본 이름을 바꾼다 (임시 이름 2단계, 실패 시 되돌림).</summary>
    Rename,
}

public sealed record RenumberItem(string SourcePath, string NewName);

/// <summary>계획: 바꿀 항목과 실행 전에 알려야 할 문제.</summary>
public sealed record RenumberPlan(IReadOnlyList<RenumberItem> Items, IReadOnlyList<string> Errors)
{
    public bool CanRun => Errors.Count == 0 && Items.Count > 0;
}

public sealed record RenumberResult(int Succeeded, IReadOnlyList<string> Errors, bool RolledBack, string? OutputFolder);

/// <summary>
/// seq-renumber 도구 이식. 계획(<see cref="Plan"/>)은 순수 계산이라 미리보기에 쓰고, 실행(<see cref="Execute"/>)은 계획을 그대로 적용한다.
/// </summary>
public static class SequenceRenumberer
{
    public const string TempSuffix = ".tmp_rename";

    public static string CopyFolderFor(string sourceFolder) => sourceFolder.TrimEnd('\\', '/') + "_renamed";

    /// <summary>
    /// 새 이름을 계산하고 충돌을 찾는다. <paramref name="existingNames"/>는 대상 폴더에 이미 있는 이름(이름 바꾸기 모드에서는 원본 폴더 전체).
    /// </summary>
    public static RenumberPlan Plan(ImageSequence sequence, RenumberOptions options, RenumberMode mode, IEnumerable<string> existingNames)
    {
        var errors = new List<string>();
        var prefix = options.NewPrefix ?? sequence.Prefix;
        var separator = options.NewSeparator ?? sequence.Separator;
        var padding = options.Padding > 0 ? options.Padding : sequence.Padding;

        if (prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || separator.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            errors.Add(Localization.L.T("접두어나 구분자에 파일 이름으로 쓸 수 없는 문자가 있습니다."));
        if (prefix.Length == 0 && separator.Length == 0 && options.NewPrefix is not null)
            errors.Add(Localization.L.T("접두어를 비우려면 구분자도 함께 지정하세요."));
        if (options.Renumber && (long)options.StartFrame + sequence.Frames.Count - 1 > int.MaxValue)
            errors.Add(Localization.L.T("시작 번호가 너무 큽니다."));

        var items = new List<RenumberItem>(sequence.Frames.Count);
        for (var i = 0; i < sequence.Frames.Count && errors.Count == 0; i++)
        {
            var frame = sequence.Frames[i];
            var number = options.Renumber ? options.StartFrame + i : frame.Number;
            items.Add(new RenumberItem(frame.Path, SequenceName.Format(prefix, separator, number, padding, sequence.Extension)));
        }

        // 새 이름끼리 중복 (예: 자릿수를 줄여 같은 이름이 되는 경우)
        var duplicate = items.GroupBy(i => i.NewName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) errors.Add(Localization.L.F("새 이름이 겹칩니다: {0}", duplicate.Key));

        // 시퀀스에 속하지 않은 기존 파일을 덮어쓰게 되는 경우
        var own = new HashSet<string>(sequence.Frames.Select(f => Path.GetFileName(f.Path)), StringComparer.OrdinalIgnoreCase);
        var existing = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        if (mode == RenumberMode.Rename) existing.ExceptWith(own);
        var clash = items.FirstOrDefault(i => existing.Contains(i.NewName));
        if (clash is not null) errors.Add(Localization.L.F("'{0}'이(가) 이미 있어 덮어쓰게 됩니다.", clash.NewName));

        return new RenumberPlan(errors.Count == 0 ? items : [], errors);
    }

    /// <summary>계획 실행. 이름 바꾸기 모드는 모든 파일을 임시 이름으로 바꾼 뒤 새 이름으로 바꾸고, 첫 단계가 실패하면 되돌린다.</summary>
    public static RenumberResult Execute(RenumberPlan plan, RenumberMode mode, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!plan.CanRun) return new RenumberResult(0, plan.Errors, false, null);
        return mode == RenumberMode.Copy ? ExecuteCopy(plan, progress, cancellationToken) : ExecuteRename(plan, progress, cancellationToken);
    }

    private static RenumberResult ExecuteCopy(RenumberPlan plan, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var sourceFolder = Path.GetDirectoryName(plan.Items[0].SourcePath)!;
        var target = CopyFolderFor(sourceFolder);
        Directory.CreateDirectory(target);
        var errors = new List<string>();
        var done = 0;
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // 같은 이름이 생겼으면 덮어쓰지 않는다 (계획 이후 다른 프로그램이 만든 경우)
                File.Copy(item.SourcePath, Path.Combine(target, item.NewName), overwrite: false);
                done++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(item.SourcePath)}: {ex.Message}");
            }

            progress?.Report(done + errors.Count);
        }

        return new RenumberResult(done, errors, false, target);
    }

    private static RenumberResult ExecuteRename(RenumberPlan plan, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var temps = new List<(string Original, string Temp, string Final)>(plan.Items.Count);

        // 1단계: 임시 이름. 하나라도 실패하면 전부 원래대로
        try
        {
            foreach (var item in plan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dir = Path.GetDirectoryName(item.SourcePath)!;
                var temp = Path.Combine(dir, Path.GetFileName(item.SourcePath) + TempSuffix);
                File.Move(item.SourcePath, temp, overwrite: false);
                temps.Add((item.SourcePath, temp, Path.Combine(dir, item.NewName)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            var rollbackErrors = new List<string> { ex is OperationCanceledException ? Localization.L.T("취소했습니다.") : ex.Message };
            for (var i = temps.Count - 1; i >= 0; i--)
            {
                try { File.Move(temps[i].Temp, temps[i].Original, overwrite: false); }
                catch (Exception rex) when (rex is IOException or UnauthorizedAccessException) { rollbackErrors.Add($"{temps[i].Temp}: {rex.Message}"); }
            }

            return new RenumberResult(0, rollbackErrors, true, null);
        }

        // 2단계: 최종 이름. 실패한 항목은 원래 이름으로 돌려 둔다 (임시 이름이 남지 않게)
        var errors = new List<string>();
        var done = 0;
        foreach (var (original, temp, final) in temps)
        {
            try
            {
                File.Move(temp, final, overwrite: false);
                done++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(final)}: {ex.Message}");
                try { File.Move(temp, original, overwrite: false); }
                catch (Exception rex) when (rex is IOException or UnauthorizedAccessException) { errors.Add($"{temp}: {rex.Message}"); }
            }

            progress?.Report(done + errors.Count);
        }

        return new RenumberResult(done, errors, false, null);
    }
}

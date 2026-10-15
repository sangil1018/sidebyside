using Microsoft.Win32;
using SideBySide.Shell.Abstractions;

namespace SideBySide.Shell.Services;

/// <summary>
/// 폴더 열기 명령을 현재 사용자(HKCU) 범위에서 이 앱으로 바꾼다.
/// <list type="bullet">
/// <item>Directory\shell\open — 다른 프로그램·실행 창이 폴더를 "열기"로 열 때</item>
/// <item>Folder\shell\opennewwindow — 탐색기 우클릭 "새 창에서 열기". 시스템 기본값이 DelegateExecute로 탐색기를 부르므로 빈 값으로 덮어써야 명령이 쓰인다</item>
/// </list>
/// 바꾸기 전 값은 각 동사 키에 기억해 두고, 해제하면 그대로 되돌린다. 이 앱이 만든 빈 키만 지운다.
/// Explorer 자체(Win+E, 작업 표시줄, 탐색기 안에서 더블클릭)는 바뀌지 않는다.
/// </summary>
public sealed class FileManagerRegistration : IFileManagerRegistration
{
    private sealed record Target(string ClassKey, string Verb, bool OverrideDelegate)
    {
        public string ShellKey => ClassKey + @"\shell";
        public string VerbKey => ShellKey + @"\" + Verb;
        public string CommandKey => VerbKey + @"\command";
    }

    private static readonly Target[] Targets =
    [
        new(@"Software\Classes\Directory", "open", OverrideDelegate: false),
        new(@"Software\Classes\Folder", "opennewwindow", OverrideDelegate: true),
    ];

    private const string DelegateExecute = "DelegateExecute";

    private const string OwnedValue = "SideBySideOwned";            // 1 = 이 앱이 바꾼 상태
    private const string HadBackupValue = "SideBySideHadBackup";    // 1 = 바꾸기 전 command 기본값이 있었음
    private const string BackupValue = "SideBySideBackup";          // 바꾸기 전 command 기본값
    private const string HadDelegateValue = "SideBySideHadDelegate"; // 1 = 바꾸기 전 command에 DelegateExecute가 있었음
    private const string DelegateBackupValue = "SideBySideDelegateBackup";

    public bool IsEnabled => Targets.Any(IsOwned);

    public void Enable(string executablePath)
    {
        foreach (var target in Targets)
        {
            if (!IsOwned(target))
            {
                // 처음 바꿀 때만 원래 값을 기억한다 (이미 바꾼 상태에서 경로만 갱신하는 경우는 원본을 덮지 않음)
                string? previous;
                string? previousDelegate;
                using (var before = Registry.CurrentUser.OpenSubKey(target.CommandKey))
                {
                    previous = before?.GetValue(null) as string;
                    previousDelegate = before?.GetValue(DelegateExecute) as string;
                }

                using var verb = Registry.CurrentUser.CreateSubKey(target.VerbKey);
                verb.SetValue(HadBackupValue, previous is null ? 0 : 1, RegistryValueKind.DWord);
                verb.SetValue(BackupValue, previous ?? string.Empty, RegistryValueKind.String);
                verb.SetValue(HadDelegateValue, previousDelegate is null ? 0 : 1, RegistryValueKind.DWord);
                verb.SetValue(DelegateBackupValue, previousDelegate ?? string.Empty, RegistryValueKind.String);
                verb.SetValue(OwnedValue, 1, RegistryValueKind.DWord);
            }

            using var command = Registry.CurrentUser.CreateSubKey(target.CommandKey);
            command.SetValue(null, $"\"{executablePath}\" \"%1\"", RegistryValueKind.String);
            if (target.OverrideDelegate) command.SetValue(DelegateExecute, string.Empty, RegistryValueKind.String);
        }
    }

    public void Disable()
    {
        foreach (var target in Targets)
        {
            if (!IsOwned(target)) continue;
            Restore(target);
            DeleteIfEmpty(target.CommandKey);
            DeleteIfEmpty(target.VerbKey);
            DeleteIfEmpty(target.ShellKey);
            DeleteIfEmpty(target.ClassKey);
        }
    }

    private static bool IsOwned(Target target)
    {
        using var verb = Registry.CurrentUser.OpenSubKey(target.VerbKey);
        return verb?.GetValue(OwnedValue) is int owned && owned == 1;
    }

    private static void Restore(Target target)
    {
        using var verb = Registry.CurrentUser.OpenSubKey(target.VerbKey, writable: true);
        if (verb is null) return;

        var hadBackup = verb.GetValue(HadBackupValue) is int had && had == 1;
        var previous = verb.GetValue(BackupValue) as string;
        var hadDelegate = verb.GetValue(HadDelegateValue) is int hadD && hadD == 1;
        var previousDelegate = verb.GetValue(DelegateBackupValue) as string;

        using (var command = verb.OpenSubKey("command", writable: true))
        {
            if (command is not null)
            {
                if (hadBackup) command.SetValue(null, previous ?? string.Empty, RegistryValueKind.String);
                else command.DeleteValue(string.Empty, throwOnMissingValue: false);

                if (hadDelegate) command.SetValue(DelegateExecute, previousDelegate ?? string.Empty, RegistryValueKind.String);
                else command.DeleteValue(DelegateExecute, throwOnMissingValue: false);
            }
        }

        foreach (var name in new[] { OwnedValue, HadBackupValue, BackupValue, HadDelegateValue, DelegateBackupValue })
            verb.DeleteValue(name, throwOnMissingValue: false);
    }

    private static void DeleteIfEmpty(string path)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(path))
        {
            if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0) return;
        }

        Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
    }
}

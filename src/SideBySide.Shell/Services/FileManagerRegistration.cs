using Microsoft.Win32;
using SideBySide.Shell.Abstractions;

namespace SideBySide.Shell.Services;

/// <summary>
/// HKCU\Software\Classes\Directory\shell\open\command 을 이 앱으로 바꾼다.
/// 바꾸기 전 값은 Backup 값에 기억해 두고, 해제하면 그대로 되돌린다.
/// Explorer 자체(Win+E, 작업 표시줄)는 바뀌지 않고, 다른 프로그램이 폴더를 "열기"로 열 때 이 앱이 쓰인다.
/// </summary>
public sealed class FileManagerRegistration : IFileManagerRegistration
{
    private const string DirectoryKey = @"Software\Classes\Directory";
    private const string ShellKey = DirectoryKey + @"\shell";
    private const string OpenKey = ShellKey + @"\open";
    private const string CommandKey = OpenKey + @"\command";

    private const string OwnedValue = "SideBySideOwned";     // 1 = 이 앱이 바꾼 상태
    private const string HadBackupValue = "SideBySideHadBackup";  // 1 = 바꾸기 전 command 값이 있었음
    private const string BackupValue = "SideBySideBackup";   // 바꾸기 전 command 값

    public bool IsEnabled
    {
        get
        {
            using var open = Registry.CurrentUser.OpenSubKey(OpenKey);
            return open?.GetValue(OwnedValue) is int owned && owned == 1;
        }
    }

    public void Enable(string executablePath)
    {
        if (!IsEnabled)
        {
            // 처음 바꿀 때만 원래 값을 기억한다 (이미 바꾼 상태에서 경로만 갱신하는 경우는 원본을 덮지 않음)
            using var before = Registry.CurrentUser.OpenSubKey(CommandKey);
            var previous = before?.GetValue(null) as string;
            using var open = Registry.CurrentUser.CreateSubKey(OpenKey);
            open.SetValue(HadBackupValue, previous is null ? 0 : 1, RegistryValueKind.DWord);
            open.SetValue(BackupValue, previous ?? string.Empty, RegistryValueKind.String);
        }

        using (var open = Registry.CurrentUser.CreateSubKey(OpenKey))
        {
            open.SetValue(OwnedValue, 1, RegistryValueKind.DWord);
        }

        using var command = Registry.CurrentUser.CreateSubKey(CommandKey);
        command.SetValue(null, $"\"{executablePath}\" \"%1\"", RegistryValueKind.String);
    }

    public void Disable()
    {
        using var open = Registry.CurrentUser.OpenSubKey(OpenKey, writable: true);
        if (open?.GetValue(OwnedValue) is not int owned || owned != 1) return;

        var hadBackup = open.GetValue(HadBackupValue) is int had && had == 1;
        var previous = open.GetValue(BackupValue) as string;

        if (hadBackup)
        {
            using var command = Registry.CurrentUser.CreateSubKey(CommandKey);
            command.SetValue(null, previous ?? string.Empty, RegistryValueKind.String);
        }
        else
        {
            Registry.CurrentUser.DeleteSubKeyTree(CommandKey, throwOnMissingSubKey: false);
        }

        open.DeleteValue(OwnedValue, throwOnMissingValue: false);
        open.DeleteValue(HadBackupValue, throwOnMissingValue: false);
        open.DeleteValue(BackupValue, throwOnMissingValue: false);
        open.Close();

        // 이 앱이 만든 빈 키만 정리한다 (이미 있던 키는 값이나 하위 키가 남아 있으므로 지워지지 않는다)
        DeleteIfEmpty(OpenKey);
        DeleteIfEmpty(ShellKey);
        DeleteIfEmpty(DirectoryKey);
    }

    private static void DeleteIfEmpty(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0) return;
        key.Close();
        Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
    }
}

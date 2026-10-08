namespace SideBySide.App.Infrastructure;

/// <summary>예기치 않은 오류를 %LOCALAPPDATA%\SideBySide\error.log에 남긴다 (문제 보고·유지보수용).</summary>
public static class ErrorLog
{
    private const long MaxBytes = 512 * 1024;

    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideBySide", "error.log");

    public static void Write(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                File.Move(FilePath, FilePath + ".old", overwrite: true);
            File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException)
        {
            // 로그 실패는 무시
        }
    }
}

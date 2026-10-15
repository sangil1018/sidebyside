using System.Globalization;
using SideBySide.Core.Localization;

namespace SideBySide.App.Infrastructure;

public static class Formatting
{
    private static readonly string[] Units = [L.T("바이트"), "KB", "MB", "GB", "TB"];

    /// <summary>탐색기 목록 형식: 항상 KB 단위, 올림, 천 단위 구분 ("1,234 KB").</summary>
    public static string SizeInKb(long bytes)
        => bytes == 0 ? "0 KB" : ((bytes + 1023) / 1024).ToString("N0", CultureInfo.CurrentCulture) + " KB";

    /// <summary>사람이 읽기 쉬운 크기 ("1.5 MB").</summary>
    public static string Size(long bytes)
    {
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} {Units[0]}" : $"{value:0.#} {Units[unit]}";
    }

    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
}

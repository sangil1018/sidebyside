using System.Globalization;
using SideBySide.Core.Settings;

namespace SideBySide.Core.Localization;

/// <summary>
/// 화면 문자열. 한국어 원문을 그대로 키로 쓰고, 영어는 <see cref="English"/> 표에서 찾는다 (없으면 원문).
/// 언어는 시작할 때 한 번 정한다 (바꾸면 다시 시작해야 적용). XAML에서는 <c>{l:T '원문'}</c>.
/// 새 문자열을 추가하면 영어 표에도 한 줄 추가한다.
/// </summary>
public static partial class L
{
    public static bool IsEnglish { get; private set; }

    public static void Use(AppLanguage language) => IsEnglish = language == AppLanguage.English;

    /// <summary>번역된 문자열.</summary>
    public static string T(string korean) => Translate(korean, IsEnglish);

    /// <summary>번역한 뒤 <see cref="string.Format(IFormatProvider, string, object[])"/>. 원문의 {0}, {1:N0} 등은 영어에도 같은 번호로 둔다.</summary>
    public static string F(string koreanFormat, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(koreanFormat), args);

    internal static string Translate(string korean, bool english)
        => english && (English.TryGetValue(korean, out var text) || EnglishModules.TryGetValue(korean, out text)) ? text : korean;

    internal static IReadOnlyDictionary<string, string> EnglishTable => English.Concat(EnglishModules).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
}

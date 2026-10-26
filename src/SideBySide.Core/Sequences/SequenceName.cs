using System.Globalization;

namespace SideBySide.Core.Sequences;

/// <summary>
/// 시퀀스 파일 이름 한 개를 나눈 결과: <c>접두어 + 구분자 + 프레임 + . + 확장자</c>.
/// 예: <c>shot_010_0001.exr</c> → ("shot_010", "_", 1, 패딩 4, "exr"), <c>frame-0012.png</c> → ("frame", "-", 12),
/// <c>shot_-005.png</c> → 음수 프레임 -5, <c>0001.jpg</c> → 접두어 없음.
/// </summary>
public sealed record SequenceName(string Prefix, string Separator, int Frame, int Padding, string Extension)
{
    private const string SeparatorChars = " ._-";

    /// <summary>같은 시퀀스인지 가르는 키 (대소문자 무시).</summary>
    public string GroupKey => string.Concat(Prefix, Separator, "#.", Extension).ToUpperInvariant();

    /// <summary>프레임 번호가 없는 이름(확장자가 없거나 끝이 숫자가 아님)이면 false.</summary>
    public static bool TryParse(string fileName, out SequenceName name)
    {
        name = null!;
        var dot = fileName.LastIndexOf('.');
        if (dot <= 0 || dot == fileName.Length - 1) return false;

        var extension = fileName[(dot + 1)..];
        var stem = fileName.AsSpan(0, dot);

        var end = stem.Length;
        var start = end;
        while (start > 0 && char.IsAsciiDigit(stem[start - 1])) start--;
        var digits = end - start;
        if (digits == 0 || digits > 9) return false;

        var frame = int.Parse(stem[start..end], NumberStyles.None, CultureInfo.InvariantCulture);

        // 앞이 '-'이고 그 앞이 구분자(또는 맨 앞)면 음수 프레임 (seq-renumber와 같은 규칙: "shot_-005")
        var negative = start > 0 && stem[start - 1] == '-' && (start == 1 || SeparatorChars.Contains(stem[start - 2]));
        if (negative)
        {
            start--;
            frame = -frame;
        }

        var sepStart = start;
        while (sepStart > 0 && SeparatorChars.Contains(stem[sepStart - 1])) sepStart--;

        name = new SequenceName(
            stem[..sepStart].ToString(),
            stem[sepStart..start].ToString(),
            frame,
            digits,
            extension);
        return true;
    }

    /// <summary>프레임 번호를 패딩해 이름을 만든다. 음수는 부호 뒤에 패딩 (-005).</summary>
    public static string Format(string prefix, string separator, int frame, int padding, string extension)
    {
        var digits = Math.Abs((long)frame).ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(1, padding), '0');
        return string.Concat(prefix, separator, frame < 0 ? "-" : string.Empty, digits, ".", extension);
    }
}

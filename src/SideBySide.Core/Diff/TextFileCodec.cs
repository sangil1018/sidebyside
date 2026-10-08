using System.Globalization;
using System.Text;

namespace SideBySide.Core.Diff;

/// <summary>원본 파일의 인코딩·BOM·줄끝 정보. 저장 시 그대로 유지한다.</summary>
public sealed record TextFileFormat(Encoding Encoding, bool HasBom, string NewLine, bool EndsWithNewLine)
{
    public string NewLineName => NewLine switch { "\r\n" => "CRLF", "\n" => "LF", "\r" => "CR", _ => "?" };

    public string EncodingName => Encoding switch
    {
        UTF8Encoding => HasBom ? "UTF-8 BOM" : "UTF-8",
        UnicodeEncoding u => u.GetPreamble() is [0xFE, 0xFF] ? "UTF-16 BE" : "UTF-16 LE",
        UTF32Encoding => "UTF-32",
        _ => Encoding.WebName.ToUpperInvariant(),
    };
}

public sealed record TextFileContent(TextFileFormat Format, List<string> Lines, bool IsBinary, long Size);

/// <summary>텍스트 파일 읽기/쓰기. BOM → UTF-8(엄격) → 시스템 ANSI 코드페이지 순으로 인코딩 판별.</summary>
public static class TextFileCodec
{
    public const long DefaultMaxBytes = 64L * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static TextFileCodec() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding AnsiEncoding
    {
        get
        {
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage); }
            catch (ArgumentException) { return Encoding.Latin1; }
        }
    }

    public static TextFileContent Read(string path, long maxBytes = DefaultMaxBytes)
    {
        var info = new FileInfo(path);
        if (info.Length > maxBytes)
            throw new IOException($"파일이 너무 큽니다 ({info.Length / (1024 * 1024)} MB). 텍스트 비교는 {maxBytes / (1024 * 1024)} MB까지 지원합니다.");

        var bytes = File.ReadAllBytes(path);
        return Decode(bytes);
    }

    public static TextFileContent Decode(byte[] bytes)
    {
        var (encoding, bomLength) = DetectBom(bytes);
        if (encoding is null && LooksBinary(bytes))
            return new TextFileContent(new TextFileFormat(StrictUtf8, false, "\r\n", false), [], true, bytes.Length);

        string text;
        if (encoding is not null)
        {
            text = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
        }
        else
        {
            try
            {
                text = StrictUtf8.GetString(bytes);
                encoding = new UTF8Encoding(false);
            }
            catch (DecoderFallbackException)
            {
                encoding = AnsiEncoding;
                text = encoding.GetString(bytes);
            }
        }

        var (lines, newLine, endsWithNewLine) = SplitLines(text);
        return new TextFileContent(new TextFileFormat(encoding, bomLength > 0, newLine, endsWithNewLine), lines, false, bytes.Length);
    }

    public static void Write(string path, IReadOnlyList<string> lines, TextFileFormat format)
    {
        var text = string.Join(format.NewLine, lines);
        if (format.EndsWithNewLine && lines.Count > 0) text += format.NewLine;

        var body = format.Encoding.GetBytes(text);
        var preamble = format.HasBom ? format.Encoding.GetPreamble() : [];

        // 임시 파일에 쓴 뒤 교체 → 저장 도중 실패해도 원본 보존
        var temp = path + ".sbs-tmp";
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(preamble);
            fs.Write(body);
        }

        File.Move(temp, path, overwrite: true);
    }

    public static (List<string> Lines, string NewLine, bool EndsWithNewLine) SplitLines(string text)
    {
        var lines = new List<string>();
        int crlf = 0, lf = 0, cr = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                lines.Add(text[start..i]);
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
                start = i + 1;
            }
            else if (c == '\n')
            {
                lines.Add(text[start..i]);
                lf++;
                start = i + 1;
            }
        }

        var endsWithNewLine = start == text.Length && text.Length > 0;
        if (!endsWithNewLine) lines.Add(text[start..]);
        if (text.Length == 0) lines.Clear();

        var newLine = crlf >= lf && crlf >= cr ? "\r\n" : lf >= cr ? "\n" : "\r";
        if (crlf == 0 && lf == 0 && cr == 0) newLine = Environment.NewLine;
        return (lines, newLine, endsWithNewLine);
    }

    public static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var probe = bytes[..Math.Min(bytes.Length, 8192)];
        return probe.IndexOf((byte)0) >= 0;
    }

    private static (Encoding? Encoding, int BomLength) DetectBom(byte[] b)
    {
        if (b is [0xEF, 0xBB, 0xBF, ..]) return (new UTF8Encoding(true), 3);
        if (b is [0xFF, 0xFE, 0x00, 0x00, ..]) return (new UTF32Encoding(false, true), 4);
        if (b is [0xFF, 0xFE, ..]) return (new UnicodeEncoding(false, true), 2);
        if (b is [0xFE, 0xFF, ..]) return (new UnicodeEncoding(true, true), 2);
        return (null, 0);
    }
}

using System.Text;
using SideBySide.Core.Diff;

namespace SideBySide.Core.Tests;

public class TextFileCodecTests
{
    [Theory]
    [InlineData("a\r\nb\r\n", "\r\n", true, 2)]
    [InlineData("a\nb", "\n", false, 2)]
    [InlineData("", "", false, 0)]
    [InlineData("a\r\n\r\n", "\r\n", true, 2)]
    public void Split_lines(string text, string newLine, bool endsWithNewLine, int count)
    {
        var (lines, nl, ends) = TextFileCodec.SplitLines(text);
        Assert.Equal(count, lines.Count);
        if (newLine.Length > 0) Assert.Equal(newLine, nl);
        Assert.Equal(endsWithNewLine, ends);
    }

    [Fact]
    public void Roundtrip_preserves_bom_newline_and_encoding()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "a.txt");
        var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("한글\nline2\n")).ToArray();
        File.WriteAllBytes(path, original);

        var content = TextFileCodec.Read(path);
        Assert.Equal("UTF-8 BOM", content.Format.EncodingName);
        Assert.Equal("LF", content.Format.NewLineName);
        Assert.Equal(["한글", "line2"], content.Lines);

        TextFileCodec.Write(path, content.Lines, content.Format);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Binary_and_ansi_detection()
    {
        Assert.True(TextFileCodec.Decode([1, 2, 0, 3]).IsBinary);
        var ansi = TextFileCodec.Decode([0x41, 0xB0, 0xA1, 0x0D, 0x0A]); // UTF-8로 유효하지 않음
        Assert.False(ansi.IsBinary);
        Assert.IsNotType<UTF8Encoding>(ansi.Format.Encoding);
    }
}

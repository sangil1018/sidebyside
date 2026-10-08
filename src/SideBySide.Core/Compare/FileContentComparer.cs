namespace SideBySide.Core.Compare;

/// <summary>두 파일을 버퍼 단위로 읽어 바이트 비교. 첫 차이에서 즉시 종료.</summary>
public static class FileContentComparer
{
    private const int BufferSize = 128 * 1024;

    public static async Task<bool> AreEqualAsync(string leftPath, string rightPath, CancellationToken ct = default)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
            BufferSize = 0,
        };

        await using var a = new FileStream(leftPath, options);
        await using var b = new FileStream(rightPath, options);
        if (a.Length != b.Length) return false;

        var bufA = new byte[BufferSize];
        var bufB = new byte[BufferSize];
        while (true)
        {
            var readA = a.ReadAtLeastAsync(bufA, BufferSize, throwOnEndOfStream: false, ct);
            var readB = b.ReadAtLeastAsync(bufB, BufferSize, throwOnEndOfStream: false, ct);
            var na = await readA.ConfigureAwait(false);
            var nb = await readB.ConfigureAwait(false);
            if (na != nb) return false;
            if (na == 0) return true;
            if (!bufA.AsSpan(0, na).SequenceEqual(bufB.AsSpan(0, nb))) return false;
        }
    }
}

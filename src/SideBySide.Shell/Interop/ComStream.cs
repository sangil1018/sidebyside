using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace SideBySide.Shell.Interop;

/// <summary>.NET Stream을 COM IStream으로 노출 (IInitializeWithStream 미리보기 핸들러용, 읽기 전용).</summary>
[ComVisible(true)]
internal sealed class ComStream(Stream stream) : IStream
{
    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        var read = stream.Read(pv, 0, cb);
        if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, read);
    }

    public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new NotSupportedException();

    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        var pos = stream.Seek(dlibMove, (SeekOrigin)dwOrigin);
        if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, pos);
    }

    public void SetSize(long libNewSize) => throw new NotSupportedException();

    public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new NotSupportedException();

    public void Commit(int grfCommitFlags)
    {
    }

    public void Revert() => throw new NotSupportedException();

    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();

    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();

    public void Stat(out STATSTG pstatstg, int grfStatFlag)
        => pstatstg = new STATSTG { type = 2 /* STGTY_STREAM */, cbSize = stream.Length, grfMode = 0 };

    public void Clone(out IStream ppstm) => throw new NotSupportedException();
}

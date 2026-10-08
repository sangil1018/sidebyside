using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using SideBySide.Shell.Abstractions;

namespace SideBySide.Shell.Services;

/// <summary>탐색기와 상호 호환되는 파일 클립보드. UI 스레드에서 호출해야 한다.</summary>
public sealed class ShellClipboard : IFileClipboard
{
    private const string DropEffectFormat = "Preferred DropEffect";
    private const int DropEffectCopy = 5; // COPY | LINK (탐색기와 동일)
    private const int DropEffectMove = 2;

    public void SetFiles(IReadOnlyList<string> paths, bool cut)
    {
        var list = new StringCollection();
        list.AddRange([.. paths]);
        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetData(DropEffectFormat, new MemoryStream(BitConverter.GetBytes(cut ? DropEffectMove : DropEffectCopy)));
        Retry(() => Clipboard.SetDataObject(data, copy: true));
    }

    public (IReadOnlyList<string> Paths, bool Cut)? GetFiles()
    {
        (IReadOnlyList<string>, bool)? result = null;
        Retry(() =>
        {
            if (!Clipboard.ContainsFileDropList()) return;
            var files = Clipboard.GetFileDropList().Cast<string>().ToList();
            var cut = false;
            if (Clipboard.GetData(DropEffectFormat) is MemoryStream ms && ms.Length >= 4)
            {
                var buffer = new byte[4];
                ms.ReadExactly(buffer);
                var effect = BitConverter.ToInt32(buffer);
                cut = (effect & DropEffectMove) != 0 && (effect & 1) == 0;
            }

            result = (files, cut);
        });
        return result;
    }

    public void SetText(string text) => Retry(() => Clipboard.SetText(text));

    public void Clear() => Retry(Clipboard.Clear);

    /// <summary>다른 프로세스가 클립보드를 잡고 있으면 잠깐 기다렸다 재시도.</summary>
    private static void Retry(Action action)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                action();
                return;
            }
            catch (COMException) when (i < 5)
            {
                Thread.Sleep(30);
            }
            catch (COMException)
            {
                return;
            }
        }
    }
}

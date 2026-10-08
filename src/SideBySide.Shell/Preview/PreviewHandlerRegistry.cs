using Microsoft.Win32;

namespace SideBySide.Shell.Preview;

/// <summary>확장자에 등록된 Windows 미리보기 처리기(IPreviewHandler) CLSID를 레지스트리에서 찾는다.</summary>
public static class PreviewHandlerRegistry
{
    private const string HandlerKey = @"shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    public static bool TryFind(string extension, out Guid clsid)
    {
        clsid = Guid.Empty;
        if (string.IsNullOrEmpty(extension)) return false;

        var candidates = new List<string> { extension, @"SystemFileAssociations\" + extension };
        using (var extKey = Registry.ClassesRoot.OpenSubKey(extension))
        {
            if (extKey?.GetValue(null) is string progId && progId.Length > 0) candidates.Add(progId);
            if (extKey?.GetValue("PerceivedType") is string perceived && perceived.Length > 0)
                candidates.Add(@"SystemFileAssociations\" + perceived);
        }

        foreach (var root in candidates)
        {
            using var key = Registry.ClassesRoot.OpenSubKey(root + "\\" + HandlerKey);
            if (key?.GetValue(null) is string value && Guid.TryParse(value, out clsid)) return true;
        }

        return false;
    }
}

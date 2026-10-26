using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SideBySide.Shell.Abstractions;
using static SideBySide.Shell.Interop.NativeMethods;

namespace SideBySide.Shell.Services;

/// <summary>
/// 셸 우클릭 메뉴 맨 위에 넣는 앱 항목. 명령 ID는 셸 확장이 쓰는 범위(1..0x7FFF) 밖에서 매긴다.
/// 아이콘은 앱 화면과 같은 Segoe Fluent 글리프를 메뉴 글자색으로 그린 32비트 비트맵이다.
/// 메뉴를 없앤 뒤 Dispose해야 한다 (비트맵 해제).
/// </summary>
internal sealed class AppMenuItems(IntPtr ownerHandle) : IDisposable
{
    private const uint FirstId = 0x8000;

    private readonly Dictionary<uint, Action> _actions = [];
    private readonly List<IntPtr> _bitmaps = [];
    private uint _nextId = FirstId;

    /// <summary>항목들과 구분선을 메뉴 맨 위에 넣는다.</summary>
    public void InsertAtTop(IntPtr hMenu, IReadOnlyList<ShellMenuItem> items)
    {
        var position = Insert(hMenu, 0, items);
        var separator = new MENUITEMINFO { cbSize = Marshal.SizeOf<MENUITEMINFO>(), fMask = MIIM_FTYPE, fType = MFT_SEPARATOR };
        InsertMenuItem(hMenu, position, true, ref separator);
    }

    public bool TryGetAction(uint command, out Action action) => _actions.TryGetValue(command, out action!);

    private uint Insert(IntPtr hMenu, uint position, IReadOnlyList<ShellMenuItem> items)
    {
        foreach (var item in items)
        {
            var info = new MENUITEMINFO
            {
                cbSize = Marshal.SizeOf<MENUITEMINFO>(),
                fMask = MIIM_STRING | MIIM_ID | MIIM_STATE,
                dwTypeData = item.Text,
                wID = _nextId++,
                fState = item.Enabled ? 0 : MFS_DISABLED,
            };

            if (item.Children is { Count: > 0 } children)
            {
                var sub = CreatePopupMenu();
                Insert(sub, 0, children);
                info.fMask |= MIIM_SUBMENU;
                info.hSubMenu = sub; // 상위 메뉴를 DestroyMenu하면 함께 없어진다
            }
            else if (item.Invoke is { } invoke)
            {
                _actions[info.wID] = invoke;
            }

            if (item.Glyph is { Length: > 0 } glyph && RenderGlyph(glyph) is var bitmap && bitmap != IntPtr.Zero)
            {
                info.fMask |= MIIM_BITMAP;
                info.hbmpItem = bitmap;
            }

            InsertMenuItem(hMenu, position++, true, ref info);
        }

        return position;
    }

    /// <summary>글리프 → 미리 곱한 알파 32비트 DIB (메뉴 아이콘 크기, 창 DPI 반영).</summary>
    private IntPtr RenderGlyph(string glyph)
    {
        try
        {
            var dpi = ownerHandle != IntPtr.Zero ? GetDpiForWindow(ownerHandle) : 96u;
            if (dpi == 0) dpi = 96;
            var size = (int)Math.Round(16 * dpi / 96.0);
            var color = GetSysColor(COLOR_MENUTEXT);
            var brush = new SolidColorBrush(Color.FromRgb((byte)(color & 0xFF), (byte)((color >> 8) & 0xFF), (byte)((color >> 16) & 0xFF)));

            var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                size * 0.875, brush, 1.0);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
            }

            var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var pixels = new byte[size * size * 4];
            target.CopyPixels(pixels, size * 4, 0);

            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size, // 위에서 아래로
                biPlanes = 1,
                biBitCount = 32,
            };
            var bitmap = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            _bitmaps.Add(bitmap);
            return bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ExternalException)
        {
            return IntPtr.Zero; // 아이콘이 없어도 메뉴는 동작
        }
    }

    public void Dispose()
    {
        foreach (var bitmap in _bitmaps) DeleteObject(bitmap);
        _bitmaps.Clear();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}

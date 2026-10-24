using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Orbit.Shell;

/// <summary>Миниатюра файла (для фото — само фото, для остального — значок) через IShellItemImageFactory.</summary>
internal static class ShellThumbnail
{
    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
        public uint dsBitfield0;
        public uint dsBitfield1;
        public uint dsBitfield2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int size, out DIBSECTION dib);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    public static ImageSource? Get(string path, int size)
    {
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            try
            {
                if (factory.GetImage(new SIZE { cx = size, cy = size }, 0 /* SIIGBF_RESIZETOFIT */, out hbitmap) != 0)
                    return null;
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
            return FromDib(hbitmap);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or FileNotFoundException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero)
                DeleteObject(hbitmap);
        }
    }

    /// <summary>
    /// Imaging.CreateBitmapSourceFromHBitmap теряет прозрачность, поэтому копируем пиксели DIB напрямую
    /// (32 бита, предумноженная альфа).
    /// </summary>
    private static BitmapSource? FromDib(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<DIBSECTION>(), out var dib) == 0 || dib.dsBm.bmBits == IntPtr.Zero || dib.dsBm.bmBitsPixel != 32)
            return null;
        var width = dib.dsBm.bmWidth;
        var height = dib.dsBm.bmHeight;
        var stride = dib.dsBm.bmWidthBytes;
        var pixels = new byte[stride * height];
        Marshal.Copy(dib.dsBm.bmBits, pixels, 0, pixels.Length);
        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        if (dib.biHeight > 0)
        {
            // Строки снизу вверх — переворачиваем.
            source = new TransformedBitmap(source, new ScaleTransform(1, -1));
        }
        source.Freeze();
        return source;
    }
}

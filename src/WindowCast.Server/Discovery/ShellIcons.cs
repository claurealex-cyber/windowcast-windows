using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WindowCast.Server.Discovery;

/// <summary>
/// App icons as PNG via the shell's image factory, which understands both exe paths and
/// shell:AppsFolder\AppUserModelId items (Store apps), so every app kind gets a real icon.
/// </summary>
public static class ShellIcons
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; public SIZE(int x, int y) { cx = x; cy = y; } }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    private const int SIIGBF_ICONONLY = 0x4;
    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, ref BITMAP bm);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    private static readonly Dictionary<string, byte[]?> Cache = new();
    private static readonly object CacheLock = new();

    /// <summary>PNG bytes for an exe path or an AppUserModelId, or null when the shell has nothing.</summary>
    public static byte[]? GetPng(string pathOrAumid, int size = 64)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(pathOrAumid, out var cached)) return cached;
        }
        var png = Render(pathOrAumid, size);
        lock (CacheLock) Cache[pathOrAumid] = png;
        return png;
    }

    private static byte[]? Render(string pathOrAumid, int size)
    {
        var parsingName = pathOrAumid.Contains('\\') && File.Exists(pathOrAumid) ? pathOrAumid : $"shell:AppsFolder\\{pathOrAumid}";
        try
        {
            var iid = IID_IShellItemImageFactory;
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var obj);
            var factory = (IShellItemImageFactory)obj;
            var hr = factory.GetImage(new SIZE(size, size), SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out var hbm);
            if (hr != 0 || hbm == IntPtr.Zero) return null;
            try { return HBitmapToPng(hbm); }
            finally { DeleteObject(hbm); }
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? HBitmapToPng(IntPtr hbm)
    {
        var bm = new BITMAP();
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), ref bm) == 0 || bm.bmBits == IntPtr.Zero || bm.bmBitsPixel != 32)
        {
            using var fallback = Image.FromHbitmap(hbm);
            using var fs = new MemoryStream();
            fallback.Save(fs, ImageFormat.Png);
            return fs.ToArray();
        }

        // Shell bitmaps are 32bpp premultiplied with alpha; wrap the bits directly to keep transparency.
        using var src = new Bitmap(bm.bmWidth, bm.bmHeight, bm.bmWidthBytes, PixelFormat.Format32bppPArgb, bm.bmBits);
        using var copy = new Bitmap(src); // detach from the HBITMAP memory before it is deleted
        using var ms = new MemoryStream();
        copy.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}

namespace WindowCast.Server.Capture.Encoding;

/// <summary>BGRA to NV12 (BT.601 limited range), 2x2 chroma averaging. Width and height must be even.</summary>
public static class Nv12Converter
{
    public static int BufferSize(int width, int height) => width * height * 3 / 2;

    public static unsafe void Convert(byte[] bgra, int stride, int width, int height, byte[] nv12)
    {
        if ((width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("NV12 needs even dimensions");
        if (nv12.Length < BufferSize(width, height)) throw new ArgumentException("nv12 buffer too small");

        fixed (byte* src = bgra)
        fixed (byte* dst = nv12)
        {
            var yPlane = dst;
            var uvPlane = dst + width * height;

            for (var y = 0; y < height; y += 2)
            {
                var row0 = src + y * stride;
                var row1 = row0 + stride;
                var y0 = yPlane + y * width;
                var y1 = y0 + width;
                var uv = uvPlane + (y / 2) * width;

                for (var x = 0; x < width; x += 2)
                {
                    var p = x * 4;
                    // Four pixels of the 2x2 block: (b,g,r,a) each.
                    int b00 = row0[p], g00 = row0[p + 1], r00 = row0[p + 2];
                    int b01 = row0[p + 4], g01 = row0[p + 5], r01 = row0[p + 6];
                    int b10 = row1[p], g10 = row1[p + 1], r10 = row1[p + 2];
                    int b11 = row1[p + 4], g11 = row1[p + 5], r11 = row1[p + 6];

                    y0[x] = (byte)(((66 * r00 + 129 * g00 + 25 * b00 + 128) >> 8) + 16);
                    y0[x + 1] = (byte)(((66 * r01 + 129 * g01 + 25 * b01 + 128) >> 8) + 16);
                    y1[x] = (byte)(((66 * r10 + 129 * g10 + 25 * b10 + 128) >> 8) + 16);
                    y1[x + 1] = (byte)(((66 * r11 + 129 * g11 + 25 * b11 + 128) >> 8) + 16);

                    var r = (r00 + r01 + r10 + r11 + 2) >> 2;
                    var g = (g00 + g01 + g10 + g11 + 2) >> 2;
                    var bb = (b00 + b01 + b10 + b11 + 2) >> 2;
                    uv[x] = (byte)(((-38 * r - 74 * g + 112 * bb + 128) >> 8) + 128);
                    uv[x + 1] = (byte)(((112 * r - 94 * g - 18 * bb + 128) >> 8) + 128);
                }
            }
        }
    }
}

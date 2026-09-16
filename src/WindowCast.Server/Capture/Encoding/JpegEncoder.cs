using System.Drawing;
using System.Drawing.Imaging;

namespace WindowCast.Server.Capture.Encoding;

/// <summary>
/// GDI+ JPEG straight from the pinned BGRA frame (no per-frame pixel copies). Rate limited to
/// <see cref="TargetFps"/>, quality 50, matching the macOS WebSocket fallback path. Every output is a key frame.
/// </summary>
public sealed class JpegEncoder : IFrameEncoder
{
    private static readonly ImageCodecInfo Codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private readonly double _minInterval;
    private readonly EncoderParameters _params;
    private readonly MemoryStream _ms = new(512 * 1024);
    private long _lastTicks;

    public string Name => "jpeg";
    public double TargetFps { get; }
    public int Quality { get; }

    public JpegEncoder(double targetFps = 15, int quality = 50)
    {
        TargetFps = targetFps;
        Quality = quality;
        _minInterval = 1.0 / targetFps;
        _params = new EncoderParameters(1);
        _params.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
    }

    public bool ShouldEncode()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = (now - _lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (_lastTicks != 0 && elapsed < _minInterval) return false;
        _lastTicks = now;
        return true;
    }

    public int Encode(in CapturedFrame frame, Action<EncodedChunk> sink)
    {
        if (!ShouldEncode()) return 0;
        sink(EncodeNow(frame));
        return 1;
    }

    public unsafe EncodedChunk EncodeNow(in CapturedFrame frame)
    {
        _ms.SetLength(0);
        fixed (byte* p = frame.Bgra)
        {
            using var bmp = new Bitmap(frame.Width, frame.Height, frame.Stride, PixelFormat.Format32bppRgb, (IntPtr)p);
            bmp.Save(_ms, Codec, _params);
        }
        var len = (int)_ms.Length;
        var output = new byte[len];
        System.Buffer.BlockCopy(_ms.GetBuffer(), 0, output, 0, len);
        return new EncodedChunk(output, len, true, frame.Timestamp, frame.Sequence);
    }

    public void Dispose()
    {
        _params.Dispose();
        _ms.Dispose();
    }
}

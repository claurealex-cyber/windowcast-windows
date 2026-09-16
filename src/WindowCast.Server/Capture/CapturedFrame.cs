namespace WindowCast.Server.Capture;

/// <summary>
/// A CPU-side BGRA frame. The buffer is owned by the capture session and reused, so consumers must finish
/// with it before the callback returns (or copy it).
/// </summary>
public readonly struct CapturedFrame
{
    public CapturedFrame(byte[] bgra, int width, int height, int stride, TimeSpan timestamp, long sequence)
    {
        Bgra = bgra;
        Width = width;
        Height = height;
        Stride = stride;
        Timestamp = timestamp;
        Sequence = sequence;
    }

    public byte[] Bgra { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Bytes per row (always Width * 4 in our copies).</summary>
    public int Stride { get; }
    /// <summary>System-relative capture time (QPC based).</summary>
    public TimeSpan Timestamp { get; }
    public long Sequence { get; }
}

public readonly record struct EncodedChunk(byte[] Data, int Length, bool IsKeyFrame, TimeSpan Timestamp, long Sequence)
{
    public ReadOnlySpan<byte> Span => Data.AsSpan(0, Length);
}

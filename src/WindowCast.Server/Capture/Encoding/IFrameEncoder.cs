namespace WindowCast.Server.Capture.Encoding;

public interface IFrameEncoder : IDisposable
{
    string Name { get; }

    /// <summary>
    /// Encodes one BGRA frame, calling <paramref name="sink"/> once per produced chunk (zero when the encoder
    /// is rate limiting or still buffering, more than one when it flushes a backlog). Returns the chunk count.
    /// The chunk's buffer is only valid inside the callback.
    /// </summary>
    int Encode(in CapturedFrame frame, Action<EncodedChunk> sink);
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace WindowCast.Server.Capture.Encoding;

/// <summary>
/// H.264 through a Media Foundation encoder MFT, hardware (Quick Sync, NVENC, AMF) when available with the
/// Microsoft software encoder as fallback. Input is NV12 converted on the CPU; output is Annex-B with
/// SPS/PPS in front of every IDR, which is what WebCodecs and the browser fallback want.
/// </summary>
public sealed class MfH264Encoder : IFrameEncoder
{
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    private const int OutputDataBufferFormatChange = 0x100;
    private static int _mfStarted;

    private readonly IMFTransform _mft;
    private readonly IMFMediaEventGenerator? _events;
    private readonly BlockingCollection<MediaEventTypes>? _eventQueue;
    private readonly Thread? _pump;
    private readonly bool _providesSamples;
    private readonly byte[] _nv12;
    private readonly long _frameDuration;
    private byte[] _out = new byte[1024 * 1024];
    private IMFSample? _reusableOutSample;
    private int _needInputCredits;
    private volatile bool _stopping;
    private long _lastOutputTicks;
    private bool _forceKeyFrame;

    public string Name => IsHardware ? $"h264-hw ({FriendlyName})" : $"h264-sw ({FriendlyName})";
    public string FriendlyName { get; }
    public bool IsHardware { get; }
    public bool IsAsync { get; }
    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public int BitrateKbps { get; }
    public long FramesIn { get; private set; }
    public long FramesOut { get; private set; }
    public long KeyFramesOut { get; private set; }

    public MfH264Encoder(int width, int height, int fps = 30, int bitrateKbps = 6000, bool preferHardware = true)
    {
        EnsureStartup();
        Width = width & ~1;
        Height = height & ~1;
        Fps = fps;
        BitrateKbps = bitrateKbps;
        _frameDuration = 10_000_000L / fps;
        _nv12 = new byte[Nv12Converter.BufferSize(Width, Height)];

        (_mft, FriendlyName, IsHardware) = CreateTransform(preferHardware);

        IsAsync = false;
        try { IsAsync = _mft.Attributes.GetUInt32(TransformAttributeKeys.TransformAsync) == 1; } catch { }
        if (IsAsync) _mft.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);

        ConfigureTypes();
        TuneCodec();

        var info = _mft.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & ((int)OutputStreamInfoFlags.OutputStreamProvidesSamples | (int)OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;

        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        if (IsAsync)
        {
            _events = _mft.QueryInterface<IMFMediaEventGenerator>();
            _eventQueue = new BlockingCollection<MediaEventTypes>();
            _pump = new Thread(PumpEvents) { IsBackground = true, Name = "mf-h264-events" };
            _pump.Start();
        }
    }

    private static void EnsureStartup()
    {
        if (Interlocked.Exchange(ref _mfStarted, 1) == 0)
            MediaFactory.MFStartup(false).CheckError();
    }

    private static (IMFTransform, string, bool) CreateTransform(bool preferHardware)
    {
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };

        if (preferHardware)
        {
            var hw = TryActivate((uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter), input, output);
            if (hw is { } h) return (h.mft, h.name, true);
        }
        var sw = TryActivate((uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagAsyncmft | EnumFlag.EnumFlagSortandfilter), input, output)
                 ?? throw new InvalidOperationException("No H.264 encoder MFT found");
        return (sw.mft, sw.name, false);
    }

    private static (IMFTransform mft, string name)? TryActivate(uint flags, RegisterTypeInfo input, RegisterTypeInfo output)
    {
        using var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, input, output);
        foreach (var activate in activates)
        {
            string name = "unknown";
            try { name = activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute); } catch { }
            try
            {
                var mft = activate.ActivateObject<IMFTransform>();
                return (mft, name);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WindowCast: encoder '{name}' failed to activate: {ex.Message}");
            }
        }
        return null;
    }

    private void ConfigureTypes()
    {
        // Output type first (required by the H.264 encoder MFT), then input.
        var outType = MediaFactory.MFCreateMediaType();
        outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)(BitrateKbps * 1000));
        outType.Set(MediaTypeAttributeKeys.FrameSize, Pack(Width, Height));
        outType.Set(MediaTypeAttributeKeys.FrameRate, Pack(Fps, 1));
        outType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // progressive
        outType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        outType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 0u);
        outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, 77u); // eAVEncH264VProfile_Main
        _mft.SetOutputType(0, outType, 0);
        outType.Dispose();

        var inType = MediaFactory.MFCreateMediaType();
        inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        inType.Set(MediaTypeAttributeKeys.FrameSize, Pack(Width, Height));
        inType.Set(MediaTypeAttributeKeys.FrameRate, Pack(Fps, 1));
        inType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
        inType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        inType.Set(MediaTypeAttributeKeys.DefaultStride, (uint)Width);
        _mft.SetInputType(0, inType, 0);
        inType.Dispose();
    }

    private void TuneCodec()
    {
        ICodecAPI? codec = null;
        try
        {
            var iid = CodecApiGuids.IID_ICodecAPI;
            if (Marshal.QueryInterface(_mft.NativePointer, ref iid, out var ptr) == 0 && ptr != IntPtr.Zero)
            {
                codec = (ICodecAPI)Marshal.GetObjectForIUnknown(ptr);
                Marshal.Release(ptr);
            }
        }
        catch { }
        if (codec is null) return;

        TrySet(codec, CodecApiGuids.AVLowLatencyMode, true);
        TrySet(codec, CodecApiGuids.AVEncCommonRateControlMode, CodecApiGuids.RateControl_LowDelayVBR);
        TrySet(codec, CodecApiGuids.AVEncCommonMeanBitRate, (uint)(BitrateKbps * 1000));
        TrySet(codec, CodecApiGuids.AVEncMPVGOPSize, (uint)(Fps * 2));
        TrySet(codec, CodecApiGuids.AVEncMPVDefaultBPictureCount, 0u);
    }

    private static void TrySet(ICodecAPI codec, Guid api, object value)
    {
        try { var g = api; var v = value; codec.SetValue(ref g, ref v); } catch { }
    }

    private static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    private void PumpEvents()
    {
        while (!_stopping)
        {
            try
            {
                using var ev = _events!.GetEvent(0);
                _eventQueue!.Add(ev.EventType);
            }
            catch
            {
                if (!_stopping) Thread.Sleep(1);
            }
        }
    }

    /// <summary>Ask for an IDR on the next frame (a new viewer attached).</summary>
    public void RequestKeyFrame() => _forceKeyFrame = true;

    public int Encode(in CapturedFrame frame, Action<EncodedChunk> sink)
    {
        if (frame.Width < Width || frame.Height < Height)
            throw new InvalidOperationException($"Frame {frame.Width}x{frame.Height} smaller than encoder {Width}x{Height}");

        Nv12Converter.Convert(frame.Bgra, frame.Stride, Width, Height, _nv12);
        var sample = MakeInputSample(frame.Timestamp);

        if (_forceKeyFrame)
        {
            _forceKeyFrame = false;
            try { sample.Set(SampleAttributeKeys.VideoEncodePictureType, 1u); } catch { }
        }

        try
        {
            return IsAsync ? EncodeAsync(sample, frame, sink) : EncodeSync(sample, frame, sink);
        }
        finally
        {
            sample.Dispose();
        }
    }

    private IMFSample MakeInputSample(TimeSpan timestamp)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(_nv12.Length);
        buffer.Lock(out var ptr, out _, out _);
        Marshal.Copy(_nv12, 0, ptr, _nv12.Length);
        buffer.Unlock();
        buffer.CurrentLength = _nv12.Length;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        buffer.Dispose();
        sample.SampleTime = timestamp.Ticks;
        sample.SampleDuration = _frameDuration;
        return sample;
    }

    private int EncodeSync(IMFSample sample, in CapturedFrame frame, Action<EncodedChunk> sink)
    {
        _mft.ProcessInput(0, sample, 0);
        FramesIn++;
        var count = 0;
        while (TryProcessOutput(frame, out var chunk)) { sink(chunk); count++; }
        return count;
    }

    private int EncodeAsync(IMFSample sample, in CapturedFrame frame, Action<EncodedChunk> sink)
    {
        var inputPending = true;
        var count = 0;

        if (_needInputCredits > 0)
        {
            _needInputCredits--;
            _mft.ProcessInput(0, sample, 0);
            FramesIn++;
            inputPending = false;
        }

        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10; // 100 ms budget
        while (true)
        {
            var remaining = (int)((deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
            if (remaining <= 0) break;
            if (!_eventQueue!.TryTake(out var ev, remaining)) break;

            if (ev == MediaEventTypes.TransformNeedInput)
            {
                if (inputPending)
                {
                    _mft.ProcessInput(0, sample, 0);
                    FramesIn++;
                    inputPending = false;
                }
                else
                {
                    _needInputCredits++;
                    // Encoder wants more before it will emit: nothing for this frame.
                    if (count > 0 || FramesIn > FramesOut + 2) break;
                }
            }
            else if (ev == MediaEventTypes.TransformHaveOutput)
            {
                if (TryProcessOutput(frame, out var chunk)) { sink(chunk); count++; }
                if (!inputPending) break;
            }
        }
        return count;
    }

    private bool TryProcessOutput(in CapturedFrame frame, out EncodedChunk chunk)
    {
        chunk = default;
        IMFSample? outSample = null;
        if (!_providesSamples)
        {
            if (_reusableOutSample is null)
            {
                var info = _mft.GetOutputStreamInfo(0);
                _reusableOutSample = MediaFactory.MFCreateSample();
                using var buf = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, 4 * 1024 * 1024));
                _reusableOutSample.AddBuffer(buf);
            }
            outSample = _reusableOutSample;
        }

        var odb = new OutputDataBuffer { StreamID = 0, Sample = outSample! };
        var hr = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref odb, out _);

        if (hr.Code == MF_E_TRANSFORM_NEED_MORE_INPUT)
            return false;
        if (hr.Code == MF_E_TRANSFORM_STREAM_CHANGE || (odb.Status & OutputDataBufferFormatChange) != 0)
        {
            // Encoder re-negotiated its output type (rare); accept the first available and retry.
            using var t = _mft.GetOutputAvailableType(0, 0);
            _mft.SetOutputType(0, t, 0);
            return false;
        }
        hr.CheckError();

        var produced = odb.Sample ?? outSample;
        if (produced is null) return false;
        var ownsProduced = !ReferenceEquals(produced, _reusableOutSample);
        try
        {
            using var contiguous = produced.ConvertToContiguousBuffer();
            contiguous.Lock(out var ptr, out _, out var len);
            try
            {
                if (_out.Length < len) _out = new byte[Math.Max(len, _out.Length * 2)];
                Marshal.Copy(ptr, _out, 0, len);
            }
            finally
            {
                contiguous.Unlock();
            }

            var key = false;
            try { key = produced.GetUInt32(SampleAttributeKeys.CleanPoint) == 1; } catch { }
            if (!key) key = LooksLikeIdr(_out.AsSpan(0, len));

            FramesOut++;
            if (key) KeyFramesOut++;
            _lastOutputTicks = Stopwatch.GetTimestamp();
            chunk = new EncodedChunk(_out, len, key, TimeSpan.FromTicks(produced.SampleTime), frame.Sequence);
            return true;
        }
        finally
        {
            if (ownsProduced) produced.Dispose();
        }
    }

    /// <summary>Scan Annex-B NAL headers for an IDR (type 5).</summary>
    private static bool LooksLikeIdr(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                var nalType = data[i + 3] & 0x1F;
                if (nalType == 5) return true;
                i += 2;
            }
        }
        return false;
    }

    public void Dispose()
    {
        _stopping = true;
        try { _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
        try { _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        try { MediaFactory.MFShutdownObject(_mft); } catch { }
        _pump?.Join(500);
        _events?.Dispose();
        _eventQueue?.Dispose();
        _reusableOutSample?.Dispose();
        _mft.Dispose();
    }
}

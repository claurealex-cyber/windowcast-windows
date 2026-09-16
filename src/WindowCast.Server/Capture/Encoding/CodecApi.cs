using System.Runtime.InteropServices;

namespace WindowCast.Server.Capture.Encoding;

/// <summary>Minimal ICodecAPI binding (vtable order from icodecapi.h) for encoder tuning.</summary>
[ComImport]
[Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported(ref Guid api);
    [PreserveSig] int IsModifiable(ref Guid api);
    [PreserveSig] int GetParameterRange(ref Guid api, IntPtr valueMin, IntPtr valueMax, IntPtr steppingDelta);
    [PreserveSig] int GetParameterValues(ref Guid api, IntPtr values, IntPtr valuesCount);
    [PreserveSig] int GetDefaultValue(ref Guid api, IntPtr value);
    [PreserveSig] int GetValue(ref Guid api, IntPtr value);
    [PreserveSig] int SetValue(ref Guid api, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
}

internal static class CodecApiGuids
{
    public static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    public static readonly Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid AVEncCommonQuality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
    public static readonly Guid AVEncMPVGOPSize = new("95044eab-31b2-47d5-a7e3-7ac6b0a1ebd9");
    public static readonly Guid AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid AVEncVideoForceKeyFrame = new("c5af9c1c-e1e8-4f7d-a0d8-c4f2b8a4e7c1");

    public const uint RateControl_CBR = 0;
    public const uint RateControl_PeakConstrainedVBR = 1;
    public const uint RateControl_UnconstrainedVBR = 2;
    public const uint RateControl_Quality = 3;
    public const uint RateControl_LowDelayVBR = 4;
}

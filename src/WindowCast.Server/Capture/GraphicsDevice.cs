using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WindowCast.Server.Capture.Native;

namespace WindowCast.Server.Capture;

/// <summary>
/// One shared D3D11 device for every capture session plus its WinRT wrapper for Windows.Graphics.Capture.
/// The immediate context is not thread-safe, so callers copying frames take <see cref="ContextLock"/>.
/// </summary>
public sealed class GraphicsDevice : IDisposable
{
    public ID3D11Device D3D { get; }
    public ID3D11DeviceContext Context { get; }
    public IDirect3DDevice WinRT { get; }
    public object ContextLock { get; } = new();
    public string AdapterName { get; }

    private GraphicsDevice(ID3D11Device device, ID3D11DeviceContext context, IDirect3DDevice winrt, string adapterName)
    {
        D3D = device;
        Context = context;
        WinRT = winrt;
        AdapterName = adapterName;
    }

    public static GraphicsDevice Create()
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            levels, out ID3D11Device device, out ID3D11DeviceContext context).CheckError();

        // Capture frame callbacks arrive on worker threads; let D3D serialize internally as a safety net.
        using (var mt = device.QueryInterfaceOrNull<ID3D11Multithread>())
            mt?.SetMultithreadProtected(true);

        string adapterName = "unknown";
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        using (var adapter = dxgi.GetAdapter())
            adapterName = adapter.Description.Description;

        var winrt = CaptureInterop.CreateWinRTDevice(dxgi.NativePointer);
        return new GraphicsDevice(device, context, winrt, adapterName);
    }

    public void Dispose()
    {
        WinRT.Dispose();
        Context.Dispose();
        D3D.Dispose();
    }
}

using WindowCast.Server.Capture.Native;

namespace WindowCast.Server.Input;

/// <summary>
/// Browser-normalized [0,1] point over the streamed picture to a physical screen pixel. The picture is the
/// DWM extended frame of the window (exactly what Windows.Graphics.Capture delivers), or the monitor rect.
/// </summary>
public static class CoordinateMapper
{
    public static (int x, int y) ToScreen(double nx, double ny, RECT bounds)
    {
        nx = Math.Clamp(nx, 0, 1);
        ny = Math.Clamp(ny, 0, 1);
        // Map onto pixel centers so 0 hits the first pixel and 1 hits the last, never one past the edge.
        var x = bounds.Left + (int)Math.Floor(nx * bounds.Width);
        var y = bounds.Top + (int)Math.Floor(ny * bounds.Height);
        if (x >= bounds.Right) x = bounds.Right - 1;
        if (y >= bounds.Bottom) y = bounds.Bottom - 1;
        return (x, y);
    }
}

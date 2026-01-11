using System;
using System.Runtime.InteropServices;

namespace ProxyStarter.App.Helpers;

public static class DpiHelper
{
    private static readonly IntPtr PerMonitorV2Context = new(-4);
    private const int DefaultDpi = 96;

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    public static void EnsurePerMonitorV2()
    {
        try
        {
            SetProcessDpiAwarenessContext(PerMonitorV2Context);
        }
        catch
        {
            // Ignore if the context is already set or not supported.
        }
    }

    public static double GetScaleForPoint(int screenX, int screenY)
    {
        try
        {
            var monitor = MonitorFromPoint(new POINT(screenX, screenY), MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                return 1.0;
            }

            if (GetDpiForMonitor(monitor, MonitorDpiType.EffectiveDpi, out var dpiX, out _) != 0 || dpiX == 0)
            {
                return 1.0;
            }

            return dpiX / (double)DefaultDpi;
        }
        catch
        {
            return 1.0;
        }
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    private enum MonitorDpiType
    {
        EffectiveDpi = 0,
        AngularDpi = 1,
        RawDpi = 2,
        Default = EffectiveDpi
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct POINT
    {
        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }

        public readonly int X;
        public readonly int Y;
    }
}

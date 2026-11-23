using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KompasTime;

internal static class WindowsActivity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput
    {
        public uint Size;
        public uint Tick;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInput info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    public static bool IsForeground(string processName)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint processId);
        if (processId == 0)
            return false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static TimeSpan IdleTime
    {
        get
        {
            var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
            return GetLastInputInfo(ref info)
                ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Tick))
                : TimeSpan.MaxValue;
        }
    }
}

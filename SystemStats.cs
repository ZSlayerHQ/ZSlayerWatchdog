using System.Runtime.InteropServices;

namespace ZSlayerCommandCenter.Launcher;

/// <summary>
/// Lightweight system stats via Windows P/Invoke (RAM + CPU).
/// CPU uses GetSystemTimes delta between calls.
/// </summary>
internal static class SystemStats
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    private static long _prevIdle, _prevKernel, _prevUser;

    public static (double CpuPercent, double RamUsedGB, double RamTotalGB) Get()
    {
        // RAM
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        double totalGB = 0, usedGB = 0;
        if (GlobalMemoryStatusEx(ref mem))
        {
            totalGB = mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
            usedGB = (mem.ullTotalPhys - mem.ullAvailPhys) / (1024.0 * 1024.0 * 1024.0);
        }

        // CPU (delta between calls — first call returns 0)
        double cpuPercent = 0;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var idleDelta = idle - _prevIdle;
            var totalDelta = (kernel - _prevKernel) + (user - _prevUser);

            if (totalDelta > 0 && _prevIdle > 0)
            {
                cpuPercent = (1.0 - (double)idleDelta / totalDelta) * 100.0;
                cpuPercent = Math.Clamp(cpuPercent, 0, 100);
            }

            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
        }

        return (Math.Round(cpuPercent, 1), Math.Round(usedGB, 1), Math.Round(totalGB, 1));
    }
}

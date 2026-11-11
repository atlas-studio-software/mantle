using System;
using System.Runtime.InteropServices;

namespace Mantle.Core.Features.Desktop;

/// <summary>
/// The System tile's numbers, read in-process (Electron asked main's <c>sys.stats</c> over IPC every 5 s): CPU from two
/// GetSystemTimes samples, memory from GlobalMemoryStatusEx, battery from GetSystemPowerStatus. Read only; never throws.
/// </summary>
public sealed class SysStats
{
    ulong _idle, _kernel, _user;
    bool _have;

    /// One read: the CPU share since the last read (the first read samples twice, 100 ms apart).
    public DeskStats.View Read()
    {
        try {
            double cpu = Cpu();
            if (!_have) { System.Threading.Thread.Sleep(100); cpu = Cpu(); }
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref m)) return DeskStats.Failed("Windows did not say how much memory is in use (GlobalMemoryStatusEx failed)");
            double? pct = null; bool chg = false; string why = "this machine reports no battery";
            if (GetSystemPowerStatus(out var p)) {
                bool none = (p.BatteryFlag & 128) != 0 || p.BatteryFlag == 255 && p.BatteryLifePercent == 255;
                if (!none && p.BatteryLifePercent <= 100) { pct = p.BatteryLifePercent; chg = p.ACLineStatus == 1 && (p.BatteryFlag & 8) != 0; why = "ok"; }
                else if (!none) why = "Windows did not say how full the battery is";
            } else why = "Windows did not say whether there is a battery (GetSystemPowerStatus failed)";
            return DeskStats.Of(cpu, m.ullTotalPhys - m.ullAvailPhys, m.ullTotalPhys, pct, chg, why);
        } catch (Exception e) { return DeskStats.Failed(e.Message); }
    }

    double Cpu()
    {
        if (!GetSystemTimes(out var i, out var k, out var u)) return 0;
        ulong idle = i.Value, kernel = k.Value, user = u.Value;
        double pct = 0;
        if (_have) {
            ulong di = idle - _idle, dk = kernel - _kernel, du = user - _user, total = dk + du;   // kernel time includes idle
            pct = total == 0 ? 0 : Math.Clamp(100.0 * (total - di) / total, 0, 100);
        }
        _idle = idle; _kernel = kernel; _user = user; _have = true;
        return pct;
    }

    [StructLayout(LayoutKind.Sequential)] struct FILETIME { public uint Lo, Hi; public ulong Value => ((ulong)Hi << 32) | Lo; }
    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }

    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
}

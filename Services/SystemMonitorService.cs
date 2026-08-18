using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace SoVITS_GUI.Services
{
    public class DiskInfo
    {
        public string Name { get; set; } = "";
        public ulong TotalGB { get; set; }
        public ulong UsedGB { get; set; }
        public double Percent { get; set; }
    }

    public class SystemStats
    {
        public double CpuPercent { get; set; }
        public double MemoryPercent { get; set; }
        public double GpuPercent { get; set; }
        public string CpuName { get; set; } = "";
        public string GpuName { get; set; } = "";
        public string GpuShortName { get; set; } = "";
        public ulong TotalMemoryMB { get; set; }
        public ulong UsedMemoryMB { get; set; }
        public ulong GpuMemoryMB { get; set; }
        public ulong GpuUsedMemoryMB { get; set; }
        public double GpuMemoryPercent { get; set; }
        public List<DiskInfo> Disks { get; set; } = new();
        public double DiskPercent { get; set; }
        public string DiskText { get; set; } = "";
        public bool HasGpuUsage => GpuPercent > 0;
        public bool HasGpu => GpuMemoryMB > 0;
    }

    // --- DXGI GetVRAM: 绕过 WMI uint32 4GB 限制 ---
    internal static class DxgiVram
    {
        // DXGI 错误码
        private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

        private static Guid IID_IDXGIFactory1 => new("770aae78-f26f-4dba-a829-253c83d1b387");

        private const int DESC1_VRAM_OFFSET = 272; // DedicatedVideoMemory 在 DXGI_ADAPTER_DESC1 中的偏移(x64)
        private const int DESC1_SIZE = 320;

        public static ulong GetDedicatedVideoMemory()
        {
            var iid = IID_IDXGIFactory1;
            if (CreateDXGIFactory1(ref iid, out IntPtr pFactory) != 0 || pFactory == IntPtr.Zero)
                return 0;

            try
            {
                var vtable = Marshal.ReadIntPtr(pFactory);
                var enumAdapters1 = Marshal.GetDelegateForFunctionPointer<EnumAdaptersDelegate>(
                    Marshal.ReadIntPtr(vtable + 12 * IntPtr.Size));

                for (uint i = 0; i < 8; i++)
                {
                    if (enumAdapters1(pFactory, i, out IntPtr pAdapter) != 0)
                        break;

                    try
                    {
                        var avtable = Marshal.ReadIntPtr(pAdapter);
                        var getDesc1 = Marshal.GetDelegateForFunctionPointer<GetDescDelegate>(
                            Marshal.ReadIntPtr(avtable + 10 * IntPtr.Size));

                        var mem = Marshal.AllocHGlobal(DESC1_SIZE);
                        try
                        {
                            if (getDesc1(pAdapter, mem) == 0)
                            {
                                long vram = Marshal.ReadInt64(mem, DESC1_VRAM_OFFSET);
                                if (vram > 0) return (ulong)vram;
                            }
                        }
                        finally { Marshal.FreeHGlobal(mem); }
                    }
                    finally { Marshal.Release(pAdapter); }
                }
            }
            catch { }
            finally { Marshal.Release(pFactory); }
            return 0;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdaptersDelegate(IntPtr self, uint index, out IntPtr pAdapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDescDelegate(IntPtr self, IntPtr pDesc);
    }

    public class SystemMonitorService : IDisposable
    {
        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

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

        private Timer? _timer;
        private PerformanceCounter? _cpuCounter;
        private DispatcherQueue? _dispatcherQueue;
        private bool _disposed;

        public event EventHandler<SystemStats>? StatsUpdated;

        public void Start(DispatcherQueue dispatcher, TimeSpan? interval = null)
        {
            _dispatcherQueue = dispatcher;
            _timer = new Timer(OnTimerTick, null, 200, (int)(interval ?? TimeSpan.FromSeconds(2)).TotalMilliseconds);
        }

        private void OnTimerTick(object? state)
        {
            if (_disposed || _dispatcherQueue == null) return;

            var stats = CollectStats();
            _dispatcherQueue.TryEnqueue(() => StatsUpdated?.Invoke(this, stats));
        }

        private static string? _cpuName;

        private SystemStats CollectStats()
        {
            var stats = new SystemStats();

            // CPU 型号（仅首次）
            if (_cpuName == null)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        _cpuName = obj["Name"]?.ToString()?.Trim() ?? "";
                        break;
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"CPU detection failed: {ex.Message}"); _cpuName = ""; }
            }
            stats.CpuName = _cpuName ?? "";

            // CPU 使用率
            if (_cpuCounter == null)
            {
                try
                {
                    _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                    _cpuCounter.NextValue();
                }
                catch { _cpuCounter = null; }
            }
            if (_cpuCounter != null)
            {
                try { stats.CpuPercent = Math.Round(_cpuCounter.NextValue(), 1); }
                catch
                {
                    _cpuCounter.Dispose();
                    _cpuCounter = null;
                }
            }
            if (_cpuCounter == null)
                stats.CpuPercent = EstimateCpuUsage();

            // 内存
            var memStatus = new MEMORYSTATUSEX();
            memStatus.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                stats.TotalMemoryMB = memStatus.ullTotalPhys / (1024 * 1024);
                stats.UsedMemoryMB = (memStatus.ullTotalPhys - memStatus.ullAvailPhys) / (1024 * 1024);
                stats.MemoryPercent = Math.Round(
                    (double)(memStatus.ullTotalPhys - memStatus.ullAvailPhys) / memStatus.ullTotalPhys * 100, 1);
            }

            // GPU
            CollectGpuInfo(stats);

            // 硬盘
            CollectDiskInfo(stats);

            return stats;
        }

        private static string? _dedicatedGpuName;
        private static ulong _dedicatedGpuRam;

        private void CollectGpuInfo(SystemStats stats)
        {
            // 一次性检测独显型号（仅首次）
            if (_dedicatedGpuName == null)
            {
                try
                {
                    var bestName = "";
                    ulong bestRam = 0;

                    using var searcher = new ManagementObjectSearcher(
                        "SELECT Name, PNPDeviceID FROM Win32_VideoController");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var name = obj["Name"]?.ToString() ?? "";
                        var pnpId = obj["PNPDeviceID"]?.ToString() ?? "";

                        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
                            (name.Contains("UHD") || name.Contains("Iris") || name.Contains("HD Graphics")))
                            continue;
                        if (name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (pnpId.Contains("PCI\\VEN_8086", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var ram = QueryVramFromWmi(name) ?? 0UL;
                        if (ram > bestRam) { bestRam = ram; bestName = name; }
                    }

                    // 尝试 DXGI 获取精确显存
                    var dxgiRam = DxgiVram.GetDedicatedVideoMemory();
                    if (dxgiRam > bestRam) bestRam = dxgiRam;

                    if (bestRam > 0)
                    {
                        _dedicatedGpuName = bestName;
                        _dedicatedGpuRam = bestRam;
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"GPU detection failed: {ex.Message}"); }
            }

            stats.GpuName = _dedicatedGpuName ?? "";
            stats.GpuShortName = ShortenGpuName(_dedicatedGpuName);
            stats.GpuMemoryMB = _dedicatedGpuRam / (1024 * 1024);

            // 每次采集显存使用量（WMI，无 uint32 限制）
            if (stats.GpuMemoryMB > 0)
            {
                try
                {
                    double usedBytes = 0;
                    using var searcher = new ManagementObjectSearcher(
                        "SELECT DedicatedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        if (obj["DedicatedUsage"] != null)
                            usedBytes += Convert.ToDouble(obj["DedicatedUsage"]);
                    }
                    if (usedBytes > 0)
                    {
                        var usedMB = (ulong)(usedBytes / (1024 * 1024));
                        stats.GpuUsedMemoryMB = usedMB;
                        stats.GpuMemoryPercent = Math.Round(usedBytes / 1024 / 1024 / stats.GpuMemoryMB * 100, 1);
                    }
                }
                catch { }
            }
        }

        private static ulong? QueryVramFromRegistry(string pnpDeviceId)
        {
            try
            {
                using var regKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (regKey == null) return null;

                foreach (var subKeyName in regKey.GetSubKeyNames())
                {
                    using var subKey = regKey.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    var matchId = subKey.GetValue("MatchingDeviceId")?.ToString() ?? "";
                    var pnpParts = pnpDeviceId.Split('\\');
                    var pnpTail = pnpParts.Length > 0 ? pnpParts[^1] : "";
                    if (!string.IsNullOrEmpty(pnpTail) &&
                        matchId.Contains(pnpTail, StringComparison.OrdinalIgnoreCase))
                    {
                        using var hwKey = subKey.OpenSubKey("HardwareInformation");
                        if (hwKey == null) continue;
                        var ram = hwKey.GetValue("qwMemorySize");
                        if (ram is long l && l > 0) return (ulong)l;
                        if (ram is int i && i > 0) return (ulong)i;
                    }
                }
            }
            catch { }
            return null;
        }

        private static ulong? QueryVramFromWmi(string gpuNameSubstring)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT AdapterRAM FROM Win32_VideoController WHERE Name LIKE '%" +
                    gpuNameSubstring.Replace("'", "''") + "%'");
                foreach (ManagementObject obj in searcher.Get())
                {
                    if (obj["AdapterRAM"] != null)
                    {
                        var ram = Convert.ToUInt64(obj["AdapterRAM"]);
                        if (ram > 0) return ram;
                    }
                }
            }
            catch { }
            return null;
        }

        private static void CollectDiskInfo(SystemStats stats)
        {
            try
            {
                var allDrives = System.IO.DriveInfo.GetDrives();
                double maxPct = 0;
                var lines = new List<string>();

                foreach (var drive in allDrives)
                {
                    if (drive.DriveType != System.IO.DriveType.Fixed || !drive.IsReady)
                        continue;

                    var totalGB = (ulong)(drive.TotalSize / (1024 * 1024 * 1024));
                    var freeGB = (ulong)(drive.AvailableFreeSpace / (1024 * 1024 * 1024));
                    var usedGB = totalGB - freeGB;
                    var pct = totalGB > 0
                        ? Math.Round((double)usedGB / totalGB * 100, 1)
                        : 0;

                    stats.Disks.Add(new DiskInfo
                    {
                        Name = drive.Name.TrimEnd('\\'),
                        TotalGB = totalGB,
                        UsedGB = usedGB,
                        Percent = pct
                    });

                    lines.Add($"{drive.Name.TrimEnd('\\')} {usedGB}/{totalGB}G");
                    if (pct > maxPct) maxPct = pct;
                }

                stats.DiskPercent = maxPct;
                stats.DiskText = string.Join("  ", lines);
            }
            catch { }
        }

        private static string ShortenGpuName(string? fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return "";
            var n = fullName
                .Replace("NVIDIA ", "").Replace("GeForce ", "")
                .Replace("Radeon (TM) ", "").Replace("Radeon ", "")
                .Replace("Graphics", "").Replace("  ", " ").Trim();
            return n.Length > 24 ? n[..21] + "..." : n;
        }

        private static double EstimateCpuUsage()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                // 以进程启动至今的墙钟时间作分母：旧实现混用了 TickCount64（开机起的毫秒数）
                // 与 DateTime.Ticks（公元元年起 100ns），时间基准不一致会算出负数
                var elapsedMs = (DateTime.Now - process.StartTime).TotalMilliseconds;
                if (elapsedMs <= 0) return 0;
                var cpuPercent = process.TotalProcessorTime.TotalMilliseconds / (elapsedMs * Environment.ProcessorCount) * 100;
                return Math.Round(Math.Clamp(cpuPercent, 0, 100), 1);
            }
            catch { return 0; }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _timer?.Dispose();
                _cpuCounter?.Dispose();
                _disposed = true;
            }
        }
    }
}

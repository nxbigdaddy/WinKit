using System;
using System.Collections.Generic;
using Microsoft.Win32;
using WinKit.Core.Diagnostics;
using WinKit.Platform.Wmi;

namespace WinKit.Platform.Wmi
{
    public sealed class MemoryModuleInfo
    {
        public string Manufacturer { get; set; } = string.Empty;
        public string PartNumber { get; set; } = string.Empty;
        public string Slot { get; set; } = string.Empty;
        public ulong CapacityBytes { get; set; }
        public int SpeedMhz { get; set; }

        public string CapacityText => Core.Text.ByteSize.Format((long)CapacityBytes, 0);

        public string Display =>
            CapacityText + "  " + (SpeedMhz > 0 ? SpeedMhz + " MHz" : "频率未知")
            + (Manufacturer.Length > 0 ? "  " + Manufacturer : string.Empty)
            + (Slot.Length > 0 ? "  [" + Slot + "]" : string.Empty);
    }

    public sealed class SystemInfo
    {
        public string OsCaption { get; set; } = string.Empty;
        public string OsVersion { get; set; } = string.Empty;
        public string OsBuild { get; set; } = string.Empty;
        public string OsArchitecture { get; set; } = string.Empty;
        public string MachineName { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;

        public string CpuName { get; set; } = string.Empty;
        public int CpuCores { get; set; }
        public int CpuLogicalProcessors { get; set; }
        public int CpuMaxClockMhz { get; set; }

        public string BaseBoardVendor { get; set; } = string.Empty;
        public string BaseBoardProduct { get; set; } = string.Empty;
        public string BiosVendor { get; set; } = string.Empty;
        public string BiosVersion { get; set; } = string.Empty;
        public string BiosDate { get; set; } = string.Empty;

        public List<MemoryModuleInfo> MemoryModules { get; } = new List<MemoryModuleInfo>();
        public List<string> Gpus { get; } = new List<string>();

        public ulong TotalPhysicalMemoryBytes { get; set; }

        public string OsDisplay
        {
            get
            {
                var text = OsCaption;
                if (OsBuild.Length > 0) text += "  (内部版本 " + OsBuild + ")";
                if (OsArchitecture.Length > 0) text += "  " + OsArchitecture;
                return text.Trim();
            }
        }

        public string CpuDisplay
        {
            get
            {
                var text = CpuName;
                var parts = new List<string>();
                if (CpuCores > 0) parts.Add(CpuCores + " 核");
                if (CpuLogicalProcessors > 0) parts.Add(CpuLogicalProcessors + " 线程");
                if (CpuMaxClockMhz > 0) parts.Add((CpuMaxClockMhz / 1000.0).ToString("0.0") + " GHz");
                if (parts.Count > 0) text += "  " + string.Join(" / ", parts.ToArray());
                return text.Trim();
            }
        }

        public string MemoryDisplay
        {
            get
            {
                var text = TotalPhysicalMemoryBytes > 0
                    ? Core.Text.ByteSize.Format((long)TotalPhysicalMemoryBytes, 0)
                    : "—";
                if (MemoryModules.Count > 0) text += "  （" + MemoryModules.Count + " 条）";
                return text;
            }
        }

        public string BaseBoardDisplay =>
            (BaseBoardVendor + " " + BaseBoardProduct).Trim().Length > 0
                ? (BaseBoardVendor + " " + BaseBoardProduct).Trim()
                : "—";

        public string GpuDisplay => Gpus.Count > 0 ? string.Join(" / ", Gpus.ToArray()) : "—";

        public string BiosDisplay
        {
            get
            {
                var text = (BiosVendor + " " + BiosVersion).Trim();
                if (BiosDate.Length > 0) text += "  " + BiosDate;
                return text.Length > 0 ? text : "—";
            }
        }
    }

    /// <summary>
    /// 系统/硬件信息读取。WMI 是冷路径（单类查询 50~300ms），因此结果带 TTL 缓存，
    /// 并且必须在后台线程调用，绝不能在 UI 线程直接调。
    /// </summary>
    public sealed class SystemInfoProvider
    {
        private readonly TimeSpan _ttl;
        private readonly object _gate = new object();
        private SystemInfo? _cached;
        private DateTime _fetchedAt = DateTime.MinValue;

        public SystemInfoProvider(TimeSpan? ttl = null)
        {
            _ttl = ttl ?? TimeSpan.FromMinutes(10);
        }

        public SystemInfo Get(bool forceRefresh = false)
        {
            lock (_gate)
            {
                if (!forceRefresh && _cached != null && DateTime.UtcNow - _fetchedAt < _ttl)
                {
                    return _cached;
                }

                var info = Load();
                _cached = info;
                _fetchedAt = DateTime.UtcNow;
                return info;
            }
        }

        private static SystemInfo Load()
        {
            var info = new SystemInfo
            {
                MachineName = Environment.MachineName,
                UserName = Environment.UserName,
            };

            var os = WmiQuery.Query("SELECT Caption,Version,BuildNumber,OSArchitecture,TotalVisibleMemorySize FROM Win32_OperatingSystem",
                "Caption", "Version", "BuildNumber", "OSArchitecture", "TotalVisibleMemorySize");
            if (os.Count > 0)
            {
                info.OsCaption = WmiQuery.AsString(os[0], "Caption") ?? string.Empty;
                info.OsVersion = WmiQuery.AsString(os[0], "Version") ?? string.Empty;
                info.OsBuild = WmiQuery.AsString(os[0], "BuildNumber") ?? string.Empty;
                info.OsArchitecture = WmiQuery.AsString(os[0], "OSArchitecture") ?? string.Empty;
            }

            // Win10 起 WMI 的 BuildNumber 不含修订号（UBR），注册表里才有，例如 19045
            var ubr = RegistryReader.GetInt(RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR");
            if (ubr.HasValue && info.OsBuild.Length > 0)
            {
                info.OsBuild = info.OsBuild + "." + ubr.Value.ToString();
            }

            var cpu = WmiQuery.Query("SELECT Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed FROM Win32_Processor",
                "Name", "NumberOfCores", "NumberOfLogicalProcessors", "MaxClockSpeed");
            if (cpu.Count > 0)
            {
                info.CpuName = WmiQuery.AsString(cpu[0], "Name") ?? string.Empty;
                info.CpuCores = WmiQuery.AsInt32(cpu[0], "NumberOfCores");
                info.CpuLogicalProcessors = WmiQuery.AsInt32(cpu[0], "NumberOfLogicalProcessors");
                info.CpuMaxClockMhz = WmiQuery.AsInt32(cpu[0], "MaxClockSpeed");
            }

            var board = WmiQuery.Query("SELECT Manufacturer,Product FROM Win32_BaseBoard", "Manufacturer", "Product");
            if (board.Count > 0)
            {
                info.BaseBoardVendor = WmiQuery.AsString(board[0], "Manufacturer") ?? string.Empty;
                info.BaseBoardProduct = WmiQuery.AsString(board[0], "Product") ?? string.Empty;
            }

            var bios = WmiQuery.Query("SELECT Manufacturer,SMBIOSBIOSVersion,ReleaseDate FROM Win32_BIOS",
                "Manufacturer", "SMBIOSBIOSVersion", "ReleaseDate");
            if (bios.Count > 0)
            {
                info.BiosVendor = WmiQuery.AsString(bios[0], "Manufacturer") ?? string.Empty;
                info.BiosVersion = WmiQuery.AsString(bios[0], "SMBIOSBIOSVersion") ?? string.Empty;
                var raw = WmiQuery.AsString(bios[0], "ReleaseDate");
                info.BiosDate = FormatWmiDate(raw);
            }

            var memory = WmiQuery.Query("SELECT Manufacturer,PartNumber,Capacity,Speed,DeviceLocator FROM Win32_PhysicalMemory",
                "Manufacturer", "PartNumber", "Capacity", "Speed", "DeviceLocator");
            foreach (var row in memory)
            {
                var module = new MemoryModuleInfo
                {
                    Manufacturer = WmiQuery.AsString(row, "Manufacturer") ?? string.Empty,
                    PartNumber = WmiQuery.AsString(row, "PartNumber") ?? string.Empty,
                    CapacityBytes = WmiQuery.AsUInt64(row, "Capacity"),
                    SpeedMhz = WmiQuery.AsInt32(row, "Speed"),
                    Slot = WmiQuery.AsString(row, "DeviceLocator") ?? string.Empty,
                };
                info.MemoryModules.Add(module);
                info.TotalPhysicalMemoryBytes += module.CapacityBytes;
            }

            if (info.TotalPhysicalMemoryBytes == 0)
            {
                info.TotalPhysicalMemoryBytes = Metrics.MemoryInfo.Query().TotalBytes;
            }

            var gpus = WmiQuery.Query("SELECT Name FROM Win32_VideoController", "Name");
            foreach (var row in gpus)
            {
                var name = WmiQuery.AsString(row, "Name");
                if (!string.IsNullOrEmpty(name) && !info.Gpus.Contains(name!)) info.Gpus.Add(name!);
            }

            return info;
        }

        /// <summary>WMI 的 ReleaseDate 形如 20230512000000.000000+000，转成 yyyy-MM-dd。</summary>
        private static string FormatWmiDate(string? raw)
        {
            if (string.IsNullOrEmpty(raw) || raw!.Length < 8) return string.Empty;
            var text = raw.Substring(0, 8);
            DateTime parsed;
            if (DateTime.TryParseExact(text, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out parsed))
            {
                return parsed.ToString("yyyy-MM-dd");
            }
            return text;
        }
    }

    /// <summary>注册表读取小工具。热路径不写注册表，只读。</summary>
    public static class RegistryReader
    {
        public static object? GetValue(RegistryHive hive, string subKey, string name)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
                using (var key = baseKey.OpenSubKey(subKey, writable: false))
                {
                    return key?.GetValue(name);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取注册表失败：" + subKey + "\\" + name + " → " + ex.Message);
                return null;
            }
        }

        public static string? GetString(RegistryHive hive, string subKey, string name)
        {
            var value = GetValue(hive, subKey, name);
            return value?.ToString();
        }

        public static int? GetInt(RegistryHive hive, string subKey, string name)
        {
            var value = GetValue(hive, subKey, name);
            if (value == null) return null;
            if (value is int i) return i;
            int parsed;
            return int.TryParse(value.ToString(), out parsed) ? parsed : (int?)null;
        }
    }
}

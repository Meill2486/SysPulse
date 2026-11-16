using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;

namespace SysPulse;

/// <summary>
/// 系统概览使用的 CPU、内存和系统盘规格。缺少的字段为空。
/// </summary>
internal sealed record HardwareSpecs(string? Cpu, string? Memory, string? Disk)
{
    /// <summary>
    /// 读取一次硬件规格。各类设备分别查询，单项失败时仍返回其他规格。
    /// </summary>
    public static HardwareSpecs Read() => new(TryRead(ReadCpu), TryRead(ReadMemory), TryRead(ReadSystemDisk));

    private static string? TryRead(Func<string?> read)
    {
        try { return read(); }
        catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException
                                      or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private static string? ReadCpu()
    {
        using var query = new ManagementObjectSearcher(
            "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
        using var devices = query.Get();
        var names = new HashSet<string>();
        long cores = 0, threads = 0;
        foreach (ManagementObject device in devices)
        {
            using (device)
            {
                string? name = ReadName(device, "Name");
                if (name is not null)
                    names.Add(name.Replace("(R)", "").Replace("(TM)", ""));
                cores += (uint?)device["NumberOfCores"] ?? 0;
                threads += (uint?)device["NumberOfLogicalProcessors"] ?? 0;
            }
        }
        var specs = new List<string>();
        if (names.Count > 0)
            specs.Add(string.Join(" / ", names));
        if (cores > 0 && threads > 0)
            specs.Add($"{cores} 核 / {threads} 线程");
        return Join(specs);
    }

    private static string? ReadMemory()
    {
        using var query = new ManagementObjectSearcher(
            "SELECT SMBIOSMemoryType, ConfiguredClockSpeed FROM Win32_PhysicalMemory");
        using var devices = query.Get();
        var specs = new HashSet<string>();
        foreach (ManagementObject device in devices)
        {
            using (device)
            {
                // 只识别明确的 SMBIOS 类型。速度用 DDR5-5600 形式显示，避免把速率标成实时频率。
                string? type = (uint?)device["SMBIOSMemoryType"] switch
                {
                    19 => "DDR2",
                    24 => "DDR3",
                    26 => "DDR4",
                    27 => "LPDDR",
                    28 => "LPDDR2",
                    29 => "LPDDR3",
                    30 => "LPDDR4",
                    34 => "DDR5",
                    35 => "LPDDR5",
                    _ => null
                };
                if (type is not null)
                    specs.Add(device["ConfiguredClockSpeed"] is uint speed && speed > 0 ? $"{type}-{speed}" : type);
            }
        }
        return specs.Count == 0 ? null : string.Join(" / ", specs);
    }

    /// <summary>
    /// 从 Windows 所在卷追溯对应的分区和磁盘型号。
    /// </summary>
    private static string? ReadSystemDisk()
    {
        string? drive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
        if (drive is not { Length: 2 } || drive[1] != ':')
            return null;

        // 从 Windows 所在卷追溯到实际分区和磁盘，避免把另一块盘的型号显示为系统盘。
        using var logical = new ManagementObject($"Win32_LogicalDisk.DeviceID=\"{drive}\"");
        using var partitions = logical.GetRelated("Win32_DiskPartition");
        var models = new HashSet<string>();
        foreach (ManagementObject partition in partitions)
        {
            using (partition)
            using (var disks = partition.GetRelated("Win32_DiskDrive"))
            {
                foreach (ManagementObject disk in disks)
                {
                    using (disk)
                    {
                        if (ReadName(disk, "Model") is { } model)
                            models.Add(model);
                    }
                }
            }
        }
        return models.Count == 0 ? null : string.Join(" / ", models);
    }

    private static string? ReadName(ManagementObject device, string property) =>
        (device[property] as string)?.Trim() is { Length: > 0 } name ? name : null;

    private static string? Join(List<string> specs) => specs.Count == 0 ? null : string.Join(" · ", specs);
}

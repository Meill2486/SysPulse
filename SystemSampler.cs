using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SysPulse;

/// <summary>不可变整机快照：速率为 B/s，容量和本段累计为整数 B；空值不是有效的零。</summary>
internal readonly record struct SystemSample(double? Cpu, double? Memory, double? Download, double? Upload,
    ulong MemoryUsed, ulong MemoryTotal, long DiskAvailable, long DiskTotal, ulong Received, ulong Sent)
{
    internal long Timestamp { get; init; }
    internal DateTimeOffset CapturedAt { get; init; }
    internal DateTimeOffset NetworkStartedAt { get; init; }
    internal string? CpuState { get; init; }
    internal string? MemoryState { get; init; }
    internal string? NetworkState { get; init; }
    internal string? DiskState { get; init; }
    internal string? GpuState { get; init; }
    internal double? DiskRead { get; init; }
    internal double? DiskWrite { get; init; }
    internal IReadOnlyList<GpuReading> Gpus { get; init; } = Array.Empty<GpuReading>();
    internal int NetworkSegment { get; init; }
}

/// <summary>仅由整机采样域串行拥有；CPU 唯一来源为 PDH，累计不通过速率积分。</summary>
internal sealed class SystemSampler : IDisposable
{
    private readonly NativePerformance performance = new();
    private Dictionary<ulong, (ulong Received, ulong Sent)> previousNetwork = new();
    private long previousNetworkTime;
    private ulong totalReceived, totalSent;
    private Guid? selected;
    private DateTimeOffset segmentStarted = DateTimeOffset.Now;
    private int segment;
    private int networkEpoch;
    private long diskReadAt;
    private (long Available, long Total) disk;

    /// <summary>读取下一份快照；概览关闭即释放扩展计数器，容量最多每 30 秒更新。</summary>
    internal SystemSample Read(bool includeDetails, Guid? networkInterface, int scopeEpoch = 0)
    {
        if (selected != networkInterface || networkEpoch != scopeEpoch)
        {
            selected = networkInterface;
            networkEpoch = scopeEpoch;
            previousNetwork.Clear();
            previousNetworkTime = 0;
            totalReceived = totalSent = 0;
            segmentStarted = DateTimeOffset.Now;
            segment++;
        }
        var native = performance.Read(includeDetails);
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        bool memoryValid = GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical > 0;
        string? memoryState = memoryValid ? null : $"GlobalMemoryStatusEx: {Marshal.GetLastWin32Error()}";
        double? memoryPercent = memoryValid ? 100d * (1 - (double)memory.AvailablePhysical / memory.TotalPhysical) : null;
        var (down, up, networkState) = ReadNetwork();
        if (includeDetails && (diskReadAt == 0 || Stopwatch.GetElapsedTime(diskReadAt).TotalSeconds >= 30))
        {
            disk = ReadDisk();
            diskReadAt = Stopwatch.GetTimestamp();
        }
        return new(native.Cpu, memoryPercent, down, up,
            memoryValid ? memory.TotalPhysical - memory.AvailablePhysical : 0, memoryValid ? memory.TotalPhysical : 0,
            disk.Available, disk.Total, totalReceived, totalSent)
        {
            Timestamp = Stopwatch.GetTimestamp(), CapturedAt = DateTimeOffset.Now,
            NetworkStartedAt = segmentStarted, NetworkSegment = segment,
            CpuState = native.CpuError ?? (native.Cpu.HasValue ? null : "采样中"), MemoryState = memoryState,
            NetworkState = networkState, DiskState = native.DiskError, GpuState = native.GpuError,
            DiskRead = native.DiskRead, DiskWrite = native.DiskWrite, Gpus = native.Gpus
        };
    }

    /// <summary>概览关闭请求即使被快速重新打开合并，也必须释放旧扩展查询再预热。</summary>
    internal void CloseDetails() => performance.CloseDetails();

    /// <summary>休眠恢复时保留已累计的有效字节，但不跨休眠计算速率。</summary>
    internal void Reset()
    {
        performance.Reset();
        previousNetwork.Clear();
        previousNetworkTime = 0;
        diskReadAt = 0;
    }

    private (double? Down, double? Up, string? State) ReadNetwork()
    {
        var current = PhysicalNetwork.ReadCounters(selected, out string? state);
        long now = Stopwatch.GetTimestamp();
        if (current is null || current.Count == 0)
        {
            previousNetwork.Clear();
            previousNetworkTime = 0;
            return (null, null, state);
        }
        double seconds = previousNetworkTime == 0 ? 0 : Stopwatch.GetElapsedTime(previousNetworkTime, now).TotalSeconds;
        // 长缺口不归为一个实时周期，也不凭空补计断开期间的流量。
        var (received, sent, valid) = seconds is > 0 and <= 30 ? NetworkDelta(previousNetwork, current) : default;
        previousNetwork = current;
        previousNetworkTime = now;
        totalReceived = SaturatingAdd(totalReceived, received);
        totalSent = SaturatingAdd(totalSent, sent);
        return valid ? (received / seconds, sent / seconds, null) : (null, null, "建立网络基线");
    }

    /// <summary>按 LUID 取整数差分；新增、回退、断开和重连均不引入历史字节。</summary>
    internal static (ulong Received, ulong Sent, bool HasDelta) NetworkDelta(
        IReadOnlyDictionary<ulong, (ulong Received, ulong Sent)> previous,
        IReadOnlyDictionary<ulong, (ulong Received, ulong Sent)> current)
    {
        ulong received = 0, sent = 0;
        bool valid = false;
        foreach (var (id, counters) in current)
            if (previous.TryGetValue(id, out var last) && counters.Received >= last.Received && counters.Sent >= last.Sent)
            {
                received = SaturatingAdd(received, counters.Received - last.Received);
                sent = SaturatingAdd(sent, counters.Sent - last.Sent);
                valid = true;
            }
        return (received, sent, valid);
    }

    internal static ulong SaturatingAdd(ulong a, ulong b) => ulong.MaxValue - a < b ? ulong.MaxValue : a + b;

    private static (long Available, long Total) ReadDisk()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Record("卷容量", Diagnostics.Error(error));
            return (0, 0);
        }
    }

    public void Dispose() => performance.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

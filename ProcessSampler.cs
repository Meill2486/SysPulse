using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace SysPulse;

/// <summary>按可靠应用身份汇总的进程指标；无元数据时仍可立即显示基础读数。</summary>
internal sealed record ProcessRow(string Name, int Count, double? Cpu, double? Memory, double? Download, double? Upload, ImageSource? Icon)
{
    public string? ApplicationKey { get; init; }
    public string? Path { get; init; }
    public IReadOnlyList<int> Pids { get; init; } = Array.Empty<int>();
    public bool Ended { get; init; }
    public string Key => ApplicationKey ?? Name;
    /// <summary>仅用于复用界面行的 PID 槽；未知实例的实际 Key 仍逐帧独立，不用于指标或元数据缓存。</summary>
    internal string DisplayKey => Key.StartsWith("unverified:", StringComparison.Ordinal) && Pids.Count == 1
        ? $"unverified-slot:{Pids[0]}" : Key;
    public string DisplayName => Count > 1 ? $"{Name} ×{Count}" : Name;
    public string CpuText => MetricDisplay.Percent(Cpu);
    public string MemoryText => MetricDisplay.Bytes(Memory);
    public string DownloadText => MetricDisplay.Bytes(Download, rate: true);
    public string UploadText => MetricDisplay.Bytes(Upload, rate: true);
    public override string ToString() => DisplayName;
}

/// <summary>一次实例读数；身份不可验证时只允许使用本帧独立键，不保存 CPU 或元数据基线。</summary>
internal sealed record ProcessSample(ProcessIdentity? Identity, int Pid, string Name, double? Cpu, double? Memory,
    string? Path = null, ImageSource? Icon = null, bool Ended = false, string? UnverifiedKey = null);

/// <summary>由进程采样域串行拥有；实时数值与单独的有界元数据工作线程互不阻塞。</summary>
internal sealed class ProcessSampler(ProcessTraffic traffic) : IDisposable
{
    private readonly ProcessMetadata metadata = new();
    private Dictionary<ProcessIdentity, double> previous = new();
    private long lastRead;
    private long frame;
    private bool disposed;
    internal static int LogicalProcessors => (int)GetActiveProcessorCount(0xFFFF);
    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort group);

    /// <summary>丢弃 CPU 差分并暂停元数据待办；下次 Read 恢复。</summary>
    internal void Reset()
    {
        previous.Clear();
        lastRead = 0;
        metadata.SetActive(false);
    }

    /// <summary>仅枚举实时数值；路径、显示名、图标均由元数据线程异步补齐。</summary>
    public ProcessRow[] Read()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        metadata.ThrowIfFaulted();
        metadata.SetActive(true);
        var samples = new List<ProcessSample>();
        var times = new Dictionary<ProcessIdentity, double>();
        var present = new HashSet<ProcessIdentity>();
        long now = Stopwatch.GetTimestamp();
        double elapsed = lastRead == 0 ? 0 : Stopwatch.GetElapsedTime(lastRead, now).TotalSeconds;
        int cores = LogicalProcessors;
        long currentFrame = ++frame;
        // Process.GetProcesses 的意外失败由上层停止整个进程采样域；单个进程退出则跳过该项。
        long enumerationUtc = DateTime.UtcNow.Ticks;
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                if (process.Id == 0) continue;
                ProcessIdentity? identity = ProcessIdentity.Read(process);
                // 不逐项 Refresh（会重复枚举 ProcessInfo）；枚举开始后新生的 PID 先保持独立，下一帧再验证。
                if (identity is { } born && born.CreatedUtcTicks > enumerationUtc) identity = null;
                string name;
                try { name = process.ProcessName; }
                catch (Exception error) when (ProcessIdentity.IsUnavailable(error))
                {
                    Diagnostics.Record("进程名称不可用", Diagnostics.Error(error));
                    continue;
                }

                double? memory = null;
                double? cpu = null;
                try { memory = process.WorkingSet64; }
                catch (Exception error) when (ProcessIdentity.IsUnavailable(error))
                {
                    Diagnostics.Record("进程内存不可用", Diagnostics.Error(error));
                }
                if (identity is { } id)
                {
                    double? seconds = null;
                    try { seconds = process.TotalProcessorTime.TotalSeconds; }
                    catch (Exception error) when (ProcessIdentity.IsUnavailable(error))
                    {
                        Diagnostics.Record("进程 CPU 不可用", Diagnostics.Error(error));
                    }
                    bool same = false;
                    try
                    {
                        using var after = Process.GetProcessById(id.Pid);
                        same = ProcessIdentity.Read(after) == id;
                    }
                    catch (Exception error) when (ProcessIdentity.IsUnavailable(error) || error is ArgumentException)
                    {
                        Diagnostics.Record("进程实例变化", Diagnostics.Error(error));
                    }
                    if (!same) { identity = null; memory = null; }
                    else if (seconds is { } current && times.Count < ProcessMetadata.InstanceCapacity)
                    {
                        times[id] = current;
                        if (previous.TryGetValue(id, out double last))
                            cpu = CpuPercent((id.CreatedUtcTicks, last), (id.CreatedUtcTicks, current), elapsed, cores);
                    }
                }

                ProcessDetails details = identity is { } known
                    ? metadata.Get(known, name) : new(name, null, ProcessMetadata.DefaultIcon);
                if (identity is { } live)
                {
                    present.Add(live);
                    traffic.Observe(live, name, Stopwatch.GetTimestamp());
                }
                samples.Add(new(identity, process.Id, details.Name, cpu, memory, details.Path, details.Icon,
                    UnverifiedKey: $"unverified:{process.Id}:{currentFrame}"));
            }
        }
        finally
        {
            // 即使后面的实例抛出意外异常，尚未遍历到的 Process 对象也必须释放。
            foreach (var process in processes) process.Dispose();
        }
        var network = traffic.Take();
        if (network is not null)
        {
            foreach (var (identity, bytes) in network.Counts)
            {
                if (present.Contains(identity)) continue;
                // 已退出实例只使用已有缓存，不能再按相同 PID 查询新实例的路径或图标。
                var details = metadata.Cached(identity, bytes.Name);
                samples.Add(new(identity, identity.Pid, details.Name, null, null, details.Path, details.Icon, Ended: bytes.Ended));
            }
        }
        metadata.Retain(present);
        previous = times;
        lastRead = now;
        return Aggregate(samples, network);
    }

    /// <summary>纯函数：同一路径忽略大小写聚合；未知路径按实例隔离，未知创建时间按帧隔离。</summary>
    internal static string ApplicationKey(ProcessIdentity? identity, string? path, string unverifiedKey) =>
        path is not null && identity is not null ? path.ToUpperInvariant() : identity?.Key ?? unverifiedKey;

    /// <summary>纯聚合入口，供合成断言使用；每个实例只传入一次，输入路径须已经规范化。</summary>
    internal static ProcessRow[] Aggregate(IReadOnlyList<ProcessSample> samples, TrafficSlice? network)
    {
        var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in samples)
        {
            string key = ApplicationKey(sample.Identity, sample.Path, sample.UnverifiedKey ?? $"unverified:{sample.Pid}");
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = new Group(sample.Name, sample.Path, sample.Icon ?? ProcessMetadata.DefaultIcon);
            group.Count++;
            group.Pids.Add(sample.Pid);
            if (!sample.Ended)
            {
                group.HasLive = true;
                group.CpuComplete &= sample.Cpu.HasValue;
                group.MemoryComplete &= sample.Memory.HasValue;
                group.Cpu += sample.Cpu ?? 0;
                group.Memory += sample.Memory ?? 0;
            }
            group.NetworkComplete &= network is { IsComplete: true } && sample.Identity.HasValue;
            if (sample.Identity is { } identity && network is not null && network.Counts.TryGetValue(identity, out var bytes))
            {
                group.Down += bytes.Down / network.Seconds;
                group.Up += bytes.Up / network.Seconds;
            }
        }
        return groups.Select(pair => new ProcessRow(pair.Value.Name, pair.Value.Count,
            pair.Value.HasLive && pair.Value.CpuComplete ? pair.Value.Cpu : null,
            pair.Value.HasLive && pair.Value.MemoryComplete ? pair.Value.Memory : null,
            pair.Value.NetworkComplete ? pair.Value.Down : null, pair.Value.NetworkComplete ? pair.Value.Up : null,
            pair.Value.Icon)
        {
            ApplicationKey = pair.Key,
            Path = pair.Value.Path,
            Pids = Array.AsReadOnly(pair.Value.Pids.Order().ToArray()),
            Ended = !pair.Value.HasLive
        }).ToArray();
    }

    /// <summary>纯函数：仅对同一实例的有效单调计数取差，以全部处理器组的活动逻辑核数归一化。</summary>
    internal static double? CpuPercent((long Started, double Seconds) previous,
        (long Started, double Seconds) current, double elapsed, int cores)
    {
        if (!double.IsFinite(elapsed) || elapsed < 0.25 || cores <= 0 || previous.Started <= 0 || previous.Started != current.Started ||
            !double.IsFinite(previous.Seconds) || !double.IsFinite(current.Seconds) || previous.Seconds < 0 || current.Seconds < previous.Seconds)
            return null;
        double value = (current.Seconds - previous.Seconds) / elapsed / cores * 100;
        return double.IsFinite(value) ? value : null;
    }

    /// <summary>仅释放本采样器的元数据线程；共享 Traffic 的生命周期仍由 MonitorSession 管理。</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        previous.Clear();
        metadata.Dispose();
    }

    private sealed class Group(string name, string? path, ImageSource icon)
    {
        internal readonly string Name = name;
        internal readonly string? Path = path;
        internal readonly ImageSource Icon = icon;
        internal readonly HashSet<int> Pids = new();
        internal int Count;
        internal bool HasLive;
        internal bool CpuComplete = true;
        internal bool MemoryComplete = true;
        internal bool NetworkComplete = true;
        internal double Cpu;
        internal double Memory;
        internal double Down;
        internal double Up;
    }
}

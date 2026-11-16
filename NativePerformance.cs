using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SysPulse;

/// <summary>按适配器 LUID 与物理 GPU 索引标识的本轮读数；未知或失败时 Usage 为 null。</summary>
internal sealed record GpuReading(string Id, string Name, double? Usage);

/// <summary>原生性能快照；CPU／GPU 为百分比，系统卷读写为字节每秒，错误不以零或旧值代替。</summary>
internal readonly record struct PerformanceRead(double? Cpu, double? DiskRead, double? DiskWrite,
    IReadOnlyList<GpuReading> Gpus, string? CpuError, string? DiskError, string? GpuError);

/// <summary>解析后的 GPU 引擎身份；不以 engtype 合并不同引擎或不同显卡。</summary>
internal readonly record struct GpuEngineIdentity(GpuAdapterId Adapter, uint PhysicalIndex, uint EngineIndex);

/// <summary>用于纯函数聚合的单项输入，CStatus 保留 PDH 原始状态以便验证无效样本。</summary>
internal readonly record struct GpuEngineSample(string InstanceName, double Value, uint CStatus = 0);

/// <summary>
/// 由单一串行采样线程拥有的 PDH 采集器；CPU 常驻复用查询，GPU／系统卷查询严格按需启停。
/// CPU 唯一口径为 Processor Information(_Total) 的忙碌时间，而非 Processor Utility。
/// </summary>
internal sealed class NativePerformance : IDisposable
{
    private const uint Success = 0;
    private const uint MoreData = 0x800007D2;
    private const uint FormatDouble = 0x00000200;
    private const uint FormatNoCap100 = 0x00008000;
    private const uint MaxGpuBufferBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan QueryRetryInterval = TimeSpan.FromSeconds(10);
    private const string CpuPath = @"\Processor Information(_Total)\% Processor Time";
    private const string GpuPath = @"\GPU Engine(*)\Utilization Percentage";

    private PdhQueryHandle? cpuQuery;
    private PdhQueryHandle? detailQuery;
    private IntPtr cpuCounter;
    private IntPtr diskReadCounter;
    private IntPtr diskWriteCounter;
    private IntPtr gpuCounter;
    private long cpuSetupAt;
    private bool detailInitialized;
    private bool cpuPrimed;
    private bool detailPrimed;
    private bool disposed;
    private string? cpuSetupError;
    private string? diskReadSetupError;
    private string? diskWriteSetupError;
    private string? gpuSetupError;
    private GpuAdapterMetadata? adapters;

    /// <summary>
    /// 返回本轮数据；首次成功收集只建立差分基线。includeDetails=false 会先立即释放扩展查询。
    /// 无数据、失败与预热均返回 null 及中文状态，未请求的扩展字段则为空且无错误。
    /// </summary>
    public PerformanceRead Read(bool includeDetails)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!includeDetails)
            CloseDetails();

        double? cpu = ReadCpu(out string? cpuError);
        if (!includeDetails)
            return new PerformanceRead(cpu, null, null, Array.Empty<GpuReading>(), cpuError, null, null);

        return ReadDetails(cpu, cpuError);
    }

    /// <summary>关闭所有查询并作废 DXGI 缓存；下次打开概览时再枚举显卡。</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        CloseCpu();
        CloseDetails();
        adapters = null;
    }

    /// <summary>幂等释放所有查询；不得与 Read 或 Reset 并发调用。</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        CloseCpu();
        CloseDetails();
        disposed = true;
    }

    /// <summary>独立复用 CPU 查询；收集失败后重新预热，不回退到另一种 CPU 算法。</summary>
    private double? ReadCpu(out string? error)
    {
        if (cpuQuery is null && (cpuSetupAt == 0 || Stopwatch.GetElapsedTime(cpuSetupAt) >= QueryRetryInterval))
        {
            cpuSetupAt = Stopwatch.GetTimestamp();
            cpuQuery = OpenQuery("CPU", out cpuSetupError);
            if (cpuQuery is not null)
            {
                cpuCounter = AddCounter(cpuQuery, CpuPath, "CPU", out cpuSetupError);
                if (cpuCounter == IntPtr.Zero)
                {
                    cpuQuery.Dispose();
                    cpuQuery = null;
                }
            }
        }

        error = cpuSetupError;
        if (cpuQuery is null)
            return null;

        error = Collect(cpuQuery, "CPU");
        if (error is not null)
        {
            // 原查询可能已失效；限频重建同一计数器并重新预热。
            CloseCpu();
            cpuSetupError = error;
            cpuSetupAt = Stopwatch.GetTimestamp();
            return null;
        }
        if (!cpuPrimed)
        {
            cpuPrimed = true;
            error = "CPU 预热中";
            return null;
        }

        double? value = ReadValue(cpuCounter, "CPU", out error);
        GC.KeepAlive(cpuQuery); // 格式化 API 使用裸计数器，调用期间须保持拥有它的查询存活。
        return value.HasValue ? Math.Clamp(value.Value, 0, 100) : null;
    }

    /// <summary>单次收集扩展查询，逐计数器处理失败，读、写与 GPU 互不以失败值覆盖。</summary>
    private PerformanceRead ReadDetails(double? cpu, string? cpuError)
    {
        var metadata = adapters ??= GpuAdapters.Read();
        EnsureDetails();
        string? diskError = Combine(diskReadSetupError, diskWriteSetupError);
        string? gpuError = Combine(gpuSetupError, metadata.Error);
        if (detailQuery is null)
            return new PerformanceRead(cpu, null, null, Array.Empty<GpuReading>(), cpuError, diskError, gpuError);

        string? collectError = Collect(detailQuery, "扩展采样");
        if (collectError is not null)
        {
            detailPrimed = false;
            if (diskReadCounter != IntPtr.Zero || diskWriteCounter != IntPtr.Zero)
                diskError = Combine(diskError, collectError);
            if (gpuCounter != IntPtr.Zero)
                gpuError = Combine(gpuError, collectError);
            return new PerformanceRead(cpu, null, null, Array.Empty<GpuReading>(), cpuError, diskError, gpuError);
        }
        if (!detailPrimed)
        {
            detailPrimed = true;
            if (diskReadCounter != IntPtr.Zero || diskWriteCounter != IntPtr.Zero)
                diskError = Combine(diskError, "系统卷读写预热中");
            if (gpuCounter != IntPtr.Zero)
                gpuError = Combine(gpuError, "GPU 预热中");
            return new PerformanceRead(cpu, null, null, Array.Empty<GpuReading>(), cpuError, diskError, gpuError);
        }

        double? read = null, write = null;
        string? readError = diskReadSetupError, writeError = diskWriteSetupError;
        if (diskReadCounter != IntPtr.Zero)
            read = ReadValue(diskReadCounter, "系统卷读", out readError);
        if (diskWriteCounter != IntPtr.Zero)
            write = ReadValue(diskWriteCounter, "系统卷写", out writeError);
        IReadOnlyList<GpuReading> gpus = Array.Empty<GpuReading>();
        if (gpuCounter != IntPtr.Zero)
        {
            gpus = ReadGpus(metadata, out string? valueError);
            gpuError = Combine(metadata.Error, valueError);
        }
        GC.KeepAlive(detailQuery);
        return new PerformanceRead(cpu, read, write, gpus, cpuError, Combine(readError, writeError), gpuError);
    }

    /// <summary>仅在扩展需求开启时建查询；任一计数器添加失败不阻止其他计数器注册。</summary>
    private void EnsureDetails()
    {
        if (detailInitialized)
            return;
        detailInitialized = true;
        detailQuery = OpenQuery("扩展", out string? queryError);
        if (detailQuery is null)
        {
            diskReadSetupError = diskWriteSetupError = gpuSetupError = queryError;
            return;
        }

        // 不硬编码 C:，也不把 PhysicalDisk 活动时间当成系统卷吞吐。
        string? root = Path.GetPathRoot(Environment.SystemDirectory);
        if (root is { Length: 3 } && root[1] == ':' && (root[2] == '\\' || root[2] == '/'))
        {
            string volume = root.Substring(0, 2);
            diskReadCounter = AddCounter(detailQuery, $@"\LogicalDisk({volume})\Disk Read Bytes/sec", "系统卷读", out diskReadSetupError);
            diskWriteCounter = AddCounter(detailQuery, $@"\LogicalDisk({volume})\Disk Write Bytes/sec", "系统卷写", out diskWriteSetupError);
        }
        else
        {
            diskReadSetupError = diskWriteSetupError = "无法确定 Windows 所在系统卷";
        }
        gpuCounter = AddCounter(detailQuery, GpuPath, "GPU", out gpuSetupError);
        if (diskReadCounter == IntPtr.Zero && diskWriteCounter == IntPtr.Zero && gpuCounter == IntPtr.Zero)
        {
            detailQuery.Dispose();
            detailQuery = null;
        }
    }

    /// <summary>数组大小会随进程实例变化；有界重试 MORE_DATA，所有退出路径均释放非托管缓冲。</summary>
    private IReadOnlyList<GpuReading> ReadGpus(GpuAdapterMetadata metadata, out string? error)
    {
        IntPtr buffer = IntPtr.Zero;
        error = null;
        try
        {
            uint size = 0;
            uint status = PdhGetFormattedCounterArrayW(gpuCounter, FormatDouble | FormatNoCap100, ref size, out uint count, IntPtr.Zero);
            if (status == Success && count == 0)
            {
                error = "GPU 无有效引擎数据";
                return Array.Empty<GpuReading>();
            }
            if (status != MoreData)
            {
                error = status == Success ? "GPU 数组数据无效" : PdhError("GPU 数组", status);
                return Array.Empty<GpuReading>();
            }

            int stride = Marshal.SizeOf<PdhArrayItem>();
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (size == 0 || size > MaxGpuBufferBytes)
                {
                    error = "GPU 计数器缓冲大小无效";
                    return Array.Empty<GpuReading>();
                }
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                }
                uint capacity = size;
                buffer = Marshal.AllocHGlobal((int)capacity);
                status = PdhGetFormattedCounterArrayW(gpuCounter, FormatDouble | FormatNoCap100, ref size, out count, buffer);
                if (status == MoreData)
                {
                    // 实例恰在两次调用之间增加；若提供者未给出更大需求则适度扩容。
                    if (size <= capacity)
                        size = capacity * 2;
                    continue;
                }
                if (status != Success)
                {
                    error = PdhError("GPU 数组", status);
                    return Array.Empty<GpuReading>();
                }
                if (count > capacity / (uint)stride)
                {
                    error = "GPU 数组数据无效";
                    return Array.Empty<GpuReading>();
                }

                var samples = new List<GpuEngineSample>((int)count);
                for (uint index = 0; index < count; index++)
                {
                    var item = Marshal.PtrToStructure<PdhArrayItem>(IntPtr.Add(buffer, (int)index * stride));
                    string name = item.Name == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(item.Name) ?? string.Empty;
                    samples.Add(new GpuEngineSample(name, item.Value.DoubleValue, item.Value.CStatus));
                }
                return AggregateGpu(samples, metadata.Names, metadata.Excluded, out error);
            }
            error = "GPU 实例变化过快，暂未就绪";
            return Array.Empty<GpuReading>();
        }
        catch (Exception exception) when (NativeUnavailable(exception))
        {
            error = "GPU PDH 原生接口不可用";
            return Array.Empty<GpuReading>();
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>纯函数：仅 VALID_DATA／NEW_DATA 且有限、非负的值可参与读数，零是真实有效数据。</summary>
    internal static bool IsValidCounterValue(uint cStatus, double value) =>
        (cStatus == 0 || cStatus == 1) && double.IsFinite(value) && value >= 0;

    /// <summary>
    /// 纯函数：解析 pid_*_luid_0x高32位_0x低32位_phys_*_eng_*_engtype_*。
    /// 引擎类型仅作格式边界，不参与身份；名称无法完整解析时返回 false。
    /// </summary>
    internal static bool TryParseGpuInstance(string? instanceName, out GpuEngineIdentity identity)
    {
        identity = default;
        ReadOnlySpan<char> remaining = instanceName.AsSpan();
        if (!TakePrefix(ref remaining, "pid_") || !TakeNumber(ref remaining, false, out _) ||
            !TakePrefix(ref remaining, "luid_") || !TakeNumber(ref remaining, true, out uint high) ||
            !TakeNumber(ref remaining, true, out uint low) || !TakePrefix(ref remaining, "phys_") ||
            !TakeNumber(ref remaining, false, out uint physical) || !TakePrefix(ref remaining, "eng_") ||
            !TakeNumber(ref remaining, false, out uint engine) || !TakePrefix(ref remaining, "engtype_") ||
            remaining.Trim().IsEmpty)
            return false;

        identity = new GpuEngineIdentity(new GpuAdapterId(high, low), physical, engine);
        return true;
    }

    /// <summary>
    /// 纯函数：先合并同一 LUID／phys／eng 下各 PID 的有效贡献，再取各物理 GPU 最繁忙引擎。
    /// 不沿用旧值；可解析但全无效的 GPU 为 null，坏名称不入组，已排除的适配器不输出。
    /// 返回值按 LUID 高位、低位、phys 排序；未知名称显式包含 LUID，部分失败保留其他有效 GPU。
    /// </summary>
    internal static IReadOnlyList<GpuReading> AggregateGpu(
        IEnumerable<GpuEngineSample> samples,
        IReadOnlyDictionary<GpuAdapterId, string> names,
        IReadOnlySet<GpuAdapterId> excluded,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(excluded);
        var devices = new HashSet<GpuDeviceId>();
        var engines = new Dictionary<GpuEngineIdentity, double>();
        bool invalid = false, unparsed = false;
        foreach (GpuEngineSample sample in samples)
        {
            if (!TryParseGpuInstance(sample.InstanceName, out GpuEngineIdentity identity))
            {
                unparsed = true;
                continue;
            }
            if (excluded.Contains(identity.Adapter))
                continue;
            devices.Add(new GpuDeviceId(identity.Adapter, identity.PhysicalIndex));
            if (!IsValidCounterValue(sample.CStatus, sample.Value))
            {
                invalid = true;
                continue;
            }
            engines.TryGetValue(identity, out double sum);
            // 非负贡献饱和累加与最后限幅等价，同时避免极端有限输入相加溢出。
            engines[identity] = Math.Min(100, sum + Math.Min(100, sample.Value));
        }

        var busiest = new Dictionary<GpuDeviceId, double>();
        foreach (var pair in engines)
        {
            var device = new GpuDeviceId(pair.Key.Adapter, pair.Key.PhysicalIndex);
            if (!busiest.TryGetValue(device, out double current) || pair.Value > current)
                busiest[device] = pair.Value;
        }
        var ordered = new List<GpuDeviceId>(devices);
        ordered.Sort(static (left, right) =>
        {
            int result = left.Adapter.HighPart.CompareTo(right.Adapter.HighPart);
            if (result == 0)
                result = left.Adapter.LowPart.CompareTo(right.Adapter.LowPart);
            return result != 0 ? result : left.PhysicalIndex.CompareTo(right.PhysicalIndex);
        });

        error = engines.Count == 0 ? "GPU 无有效引擎数据" : null;
        if (invalid)
            error = Combine(error, "GPU 部分实例数据无效");
        if (unparsed)
            error = Combine(error, "GPU 部分实例名称无法解析");
        bool unmapped = false;
        var readings = new List<GpuReading>(ordered.Count);
        foreach (GpuDeviceId device in ordered)
        {
            if (!names.TryGetValue(device.Adapter, out string? name) || string.IsNullOrWhiteSpace(name))
            {
                name = $"未知 GPU（LUID {device.Adapter}）";
                unmapped = true;
            }
            string physical = device.PhysicalIndex.ToString(CultureInfo.InvariantCulture);
            string id = $"luid_{device.Adapter}_phys_{physical}";
            double? usage = busiest.TryGetValue(device, out double value) ? value : null;
            readings.Add(new GpuReading(id, $"{name}（物理 GPU {physical}）", usage));
        }
        if (unmapped)
            error = Combine(error, "GPU 名称未映射（见 LUID）");
        // 不把可变 List 暴露给快照消费者，也不在后续读取中修改结果。
        return readings.AsReadOnly();
    }

    private static bool TakePrefix(ref ReadOnlySpan<char> remaining, string prefix)
    {
        if (!remaining.StartsWith(prefix.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;
        remaining = remaining.Slice(prefix.Length);
        return true;
    }

    private static bool TakeNumber(ref ReadOnlySpan<char> remaining, bool hex, out uint value)
    {
        value = 0;
        int separator = remaining.IndexOf('_');
        if (separator <= 0)
            return false;
        ReadOnlySpan<char> token = remaining.Slice(0, separator);
        if (hex)
        {
            if (token.Length != 10 || !token.StartsWith("0x".AsSpan(), StringComparison.OrdinalIgnoreCase))
                return false;
            token = token.Slice(2);
        }
        if (!uint.TryParse(token, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return false;
        remaining = remaining.Slice(separator + 1);
        return true;
    }

    private static PdhQueryHandle? OpenQuery(string source, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = $"{source} PDH 仅支持 Windows";
            return null;
        }
        PdhQueryHandle? query = null;
        try
        {
            uint status = PdhOpenQueryW(null, UIntPtr.Zero, out PdhQueryHandle opened);
            query = opened;
            if (status == Success && !query.IsInvalid)
                return query;
            error = status == Success ? $"{source} 查询句柄无效" : PdhError($"{source} 查询", status);
            query.Dispose();
            return null;
        }
        catch (Exception exception) when (NativeUnavailable(exception))
        {
            query?.Dispose();
            error = $"{source} PDH 原生接口不可用";
            return null;
        }
    }

    private static IntPtr AddCounter(PdhQueryHandle query, string path, string source, out string? error)
    {
        error = null;
        try
        {
            // 唯一入口为英文计数器 API，不先尝试本地化 AddCounter，也不引入其他数据源。
            uint status = PdhAddEnglishCounterW(query, path, UIntPtr.Zero, out IntPtr counter);
            if (status == Success && counter != IntPtr.Zero)
                return counter;
            error = status == Success ? $"{source} 计数器句柄无效" : PdhError($"{source} 计数器", status);
            return IntPtr.Zero;
        }
        catch (Exception exception) when (NativeUnavailable(exception))
        {
            error = $"{source} PDH 原生接口不可用";
            return IntPtr.Zero;
        }
    }

    private static string? Collect(PdhQueryHandle query, string source)
    {
        try
        {
            uint status = PdhCollectQueryData(query);
            return status == Success ? null : PdhError(source, status);
        }
        catch (Exception exception) when (NativeUnavailable(exception))
        {
            return $"{source} PDH 原生接口不可用";
        }
    }

    /// <summary>同时检查 API 状态、单项 CStatus 与数值范围，失败不返回缓存值。</summary>
    private static double? ReadValue(IntPtr counter, string source, out string? error)
    {
        error = null;
        try
        {
            uint status = PdhGetFormattedCounterValue(counter, FormatDouble | FormatNoCap100, out _, out PdhValue value);
            if (status != Success)
            {
                error = PdhError(source, status);
                return null;
            }
            if (!IsValidCounterValue(value.CStatus, value.DoubleValue))
            {
                error = value.CStatus > 1 ? PdhError($"{source} 数据", value.CStatus) : $"{source} 数据无效";
                return null;
            }
            return value.DoubleValue;
        }
        catch (Exception exception) when (NativeUnavailable(exception))
        {
            error = $"{source} PDH 原生接口不可用";
            return null;
        }
    }

    private void CloseCpu()
    {
        cpuQuery?.Dispose();
        cpuQuery = null;
        cpuCounter = IntPtr.Zero;
        cpuSetupAt = 0;
        cpuPrimed = false;
        cpuSetupError = null;
    }

    internal void CloseDetails()
    {
        detailQuery?.Dispose();
        detailQuery = null;
        diskReadCounter = diskWriteCounter = gpuCounter = IntPtr.Zero;
        detailInitialized = detailPrimed = false;
        diskReadSetupError = diskWriteSetupError = gpuSetupError = null;
    }

    private static bool NativeUnavailable(Exception exception) =>
        exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    private static string PdhError(string source, uint status) => $"{source}失败（0x{status:X8}）";

    private static string? Combine(string? first, string? second) =>
        first is null ? second : second is null || first == second ? first : $"{first}；{second}";

    private readonly record struct GpuDeviceId(GpuAdapterId Adapter, uint PhysicalIndex);

    /// <summary>PDH 查询拥有其全部计数器；SafeHandle 同时覆盖遗漏 Dispose 的句柄释放。</summary>
    private sealed class PdhQueryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public PdhQueryHandle() : base(true) { }
        protected override bool ReleaseHandle() => PdhCloseQuery(handle) == Success;
    }

    // PDH_FMT_COUNTERVALUE 的 union 在 Windows ABI 中从偏移 8 开始，不能按 uint 后紧接 double 打包。
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PdhValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhArrayItem
    {
        public IntPtr Name;
        public PdhValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, UIntPtr userData, out PdhQueryHandle query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(PdhQueryHandle query, string path, UIntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(PdhQueryHandle query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
}

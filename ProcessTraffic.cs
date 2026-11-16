using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace SysPulse;

/// <summary>捕获到的整数字节；未知归属仍保留字节数，不用速率反推或补造丢失事件。</summary>
internal readonly record struct TrafficBytes(long Down, long Up)
{
    internal TrafficBytes Add(long bytes, bool upload) => upload
        ? new(Down, checked(Up + bytes)) : new(checked(Down + bytes), Up);
}

/// <summary>已验证实例本周期的字节及退出标志；显示名只用于无路径的独立结算行。</summary>
internal sealed record TrafficCount(long Down, long Up, string Name, bool Ended);

/// <summary>单周期不可变结算；不完整周期的已捕获字节仅列入未归属，不作为完整进程速率展示。</summary>
internal sealed record TrafficSlice(IReadOnlyDictionary<ProcessIdentity, TrafficCount> Counts, double Seconds,
    bool IsComplete, long LostEvents, TrafficBytes Unattributed, bool AttributionLimited = false,
    bool LossCounterReset = false, bool QueueOverflow = false);

/// <summary>纯内存 ETW 账本；由调用方串行化，按创建时间和生命周期边界隔离复用 PID 与迟到事件。</summary>
internal sealed class ProcessTrafficLedger
{
    internal const int InstanceCapacity = 4096;
    internal const int EventsCapacity = 65536;
    internal const int SettlementSeconds = 5;
    internal static bool SettlementExpired(long endedQpc, long nowQpc) => Stopwatch.GetElapsedTime(endedQpc, nowQpc).TotalSeconds > SettlementSeconds;
    private const int GenerationsPerPid = 4;
    private readonly Dictionary<int, List<Lifetime>> lifetimes = new();
    private readonly List<NetworkEvent> events = new();
    private TrafficBytes unattributed;
    private bool limited, overflow;
    private int instanceCount;
    private long clock;

    /// <summary>实时枚举确认的实例可补齐会话开始前的进程；创建时间不可得时不得调用。</summary>
    internal void Observe(ProcessIdentity identity, string name, long observedQpc)
    {
        if (!identity.IsValid) return;
        var list = List(identity.Pid);
        var existing = list.FirstOrDefault(item => item.Identity == identity);
        if (existing is not null) { existing.Used = ++clock; return; }
        var pending = list.Where(item => item.Identity is null && item.EndQpc is null &&
            item.StartQpc <= observedQpc && item.StartUtcTicks >= identity.CreatedUtcTicks)
            .MaxBy(item => item.StartQpc);
        if (pending is not null)
        {
            pending.Identity = identity;
            pending.Name = name;
            pending.Used = ++clock;
        }
        else
        {
            foreach (var old in list.Where(item => item.EndQpc is null && item.StartUtcTicks < identity.CreatedUtcTicks))
                old.EndQpc = observedQpc;
            Add(list, new(identity.Pid, 0, identity.CreatedUtcTicks, 0, identity, name, ++clock));
        }
    }

    /// <summary>Start 使用事件 QPC 边界；身份只能来自实际读取的创建时间，不能拿事件时间伪造。</summary>
    internal void Begin(int pid, long qpc, long utcTicks, ulong uniqueKey, ProcessIdentity? identity, string name)
    {
        if (pid <= 0) return;
        if (identity is { } id && (!id.IsValid || id.Pid != pid || id.CreatedUtcTicks > utcTicks)) identity = null;
        var list = List(pid);
        var current = list.FirstOrDefault(item => identity is not null && item.Identity == identity) ??
            list.FirstOrDefault(item => uniqueKey != 0 && item.UniqueKey == uniqueKey && item.StartQpc == qpc &&
                (identity is null || item.Identity is null || item.Identity == identity));
        if (current is null)
        {
            current = new(pid, qpc, identity?.CreatedUtcTicks ?? utcTicks, uniqueKey, identity, name, ++clock);
            Add(list, current);
        }
        else
        {
            current.StartQpc = qpc;
            current.StartUtcTicks = identity?.CreatedUtcTicks ?? utcTicks;
            current.UniqueKey = uniqueKey;
            current.Identity ??= identity;
            current.Used = ++clock;
        }
        foreach (var other in list)
        {
            if (ReferenceEquals(other, current)) continue;
            if (other.StartQpc <= qpc && other.StartUtcTicks <= utcTicks &&
                (other.EndQpc is null || other.EndQpc > qpc)) other.EndQpc = qpc;
            if (other.StartQpc > qpc && (current.EndQpc is null || current.EndQpc > other.StartQpc))
                current.EndQpc = other.StartQpc;
        }
    }

    /// <summary>Stop 优先匹配内核实例键；未知键只允许关闭时间范围匹配的未绑定实例，绝不按 PID 关闭新实例。</summary>
    internal void End(int pid, long qpc, long utcTicks, ulong uniqueKey)
    {
        if (!lifetimes.TryGetValue(pid, out var list)) return;
        var candidates = list.Where(item => item.StartQpc <= qpc && (item.EndQpc is null || item.EndQpc >= qpc) &&
            utcTicks >= (item.Identity?.CreatedUtcTicks ?? item.StartUtcTicks)).ToArray();
        var current = uniqueKey == 0 ? null : candidates.FirstOrDefault(item => item.UniqueKey == uniqueKey);
        current ??= candidates.Where(item => item.UniqueKey == 0).MaxBy(item => item.Identity?.CreatedUtcTicks ?? item.StartUtcTicks);
        if (current is not null) current.EndQpc = qpc;
    }

    /// <summary>周期内先保存有界原始事件，结算时再关联，允许本周期稍晚到达的生命周期修正归属。</summary>
    internal void Add(int pid, long qpc, long utcTicks, int bytes, bool upload)
    {
        if (bytes <= 0) return;
        if (events.Count < EventsCapacity) events.Add(new(pid, qpc, utcTicks, bytes, upload));
        else { unattributed = unattributed.Add(bytes, upload); overflow = limited = true; }
    }

    /// <summary>纯结算入口；丢事件后清空身份置信链，下一轮必须重新观察，不把漏掉的 Stop 转嫁给新 PID。</summary>
    internal TrafficSlice Drain(double seconds, long nowQpc, long lostEvents = 0, bool lossCounterReset = false)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || lostEvents < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        bool identityChainIntact = lostEvents == 0 && !lossCounterReset;
        // 本地队列只截断网络事件，整段速率作废，但已观察到的进程生命周期仍可用于下一段。
        bool complete = identityChainIntact && !overflow && !limited;
        var counts = new Dictionary<ProcessIdentity, TrafficCount>();
        var unknown = unattributed;
        foreach (var item in events)
        {
            var owner = complete ? Resolve(item) : null;
            if (owner is null || owner.Identity is not { } identity) { unknown = unknown.Add(item.Bytes, item.Upload); continue; }
            counts.TryGetValue(identity, out var total);
            var bytes = new TrafficBytes(total?.Down ?? 0, total?.Up ?? 0).Add(item.Bytes, item.Upload);
            counts[identity] = new(bytes.Down, bytes.Up, owner.Name, owner.EndQpc is { } end && end <= nowQpc);
        }
        var result = new TrafficSlice(new ReadOnlyDictionary<ProcessIdentity, TrafficCount>(counts), seconds,
            complete, lostEvents, unknown, limited, lossCounterReset, overflow);
        events.Clear();
        unattributed = default;
        limited = overflow = false;
        if (!identityChainIntact) { lifetimes.Clear(); instanceCount = 0; }
        else
        {
            foreach (int pid in lifetimes.Keys.ToArray())
            {
                var list = lifetimes[pid];
                instanceCount -= list.RemoveAll(item => item.EndQpc is { } end && SettlementExpired(end, nowQpc));
                if (list.Count == 0) lifetimes.Remove(pid);
            }
        }
        return result;
    }

    private Lifetime? Resolve(NetworkEvent item)
    {
        if (!lifetimes.TryGetValue(item.Pid, out var list)) return null;
        Lifetime? owner = null;
        foreach (var candidate in list)
        {
            if (candidate.StartQpc > item.Qpc || candidate.EndQpc is { } end && item.Qpc >= end ||
                item.UtcTicks < (candidate.Identity?.CreatedUtcTicks ?? candidate.StartUtcTicks)) continue;
            // 存在未能验证的生命周期时宁可保留未归属，不能猜成同 PID 的已知应用。
            if (candidate.Identity is null) return null;
            if (owner is null || candidate.Identity.Value.CreatedUtcTicks > owner.Identity!.Value.CreatedUtcTicks) owner = candidate;
        }
        return owner;
    }

    private List<Lifetime> List(int pid)
    {
        if (!lifetimes.TryGetValue(pid, out var list)) lifetimes[pid] = list = new();
        return list;
    }

    private void Add(List<Lifetime> list, Lifetime item)
    {
        list.Add(item);
        instanceCount++;
        if (list.Count > GenerationsPerPid)
        {
            list.Remove(list.MinBy(value => value.Used)!);
            instanceCount--;
            limited = true;
        }
        if (instanceCount <= InstanceCapacity) return;
        var victim = lifetimes.Values.SelectMany(value => value).MinBy(value => value.Used)!;
        var victims = lifetimes[victim.Pid];
        victims.Remove(victim);
        if (victims.Count == 0) lifetimes.Remove(victim.Pid);
        instanceCount--;
        limited = true;
    }

    internal void Clear()
    {
        lifetimes.Clear();
        events.Clear();
        instanceCount = 0;
        unattributed = default;
        limited = overflow = false;
    }

    private sealed class Lifetime(int pid, long startQpc, long startUtcTicks, ulong uniqueKey,
        ProcessIdentity? identity, string name, long used)
    {
        internal readonly int Pid = pid;
        internal long StartQpc = startQpc;
        internal long StartUtcTicks = startUtcTicks;
        internal ulong UniqueKey = uniqueKey;
        internal ProcessIdentity? Identity = identity;
        internal string Name = name;
        internal long? EndQpc;
        internal long Used = used;
    }

    private readonly record struct NetworkEvent(int Pid, long Qpc, long UtcTicks, int Bytes, bool Upload);
}

/// <summary>唯一 TraceEvent 实时路线；控制入口串行，消费者线程独占会话销毁，只管理本实例创建的会话。</summary>
internal sealed class ProcessTraffic : IDisposable
{
    private readonly object control = new();
    private readonly object traceGate = new();
    private readonly object gate = new();
    private readonly ProcessTrafficLedger ledger = new();
    private TraceEventSession? session;
    private Task? worker;
    private ExceptionDispatchInfo? fault;
    private bool stopping = true;
    private bool disposed;
    private long lastRead;
    private long lastLost;
    private string? failure;
    private string? periodState;

    /// <summary>预期的平台／权限故障；不含路径、PID 或底层异常消息。</summary>
    public string? Failure { get { lock (gate) return failure; } }
    /// <summary>最近周期的完整性说明；与可重试的终止故障分开，供窗口状态栏显示。</summary>
    internal string? PeriodState { get { lock (gate) return periodState; } }

    /// <summary>幂等启动或重试；等待旧消费者完全释放后才允许建立新会话，不申请 UAC。</summary>
    public void Start()
    {
        lock (control)
        {
            lock (gate)
            {
                if (disposed) return;
                if (worker is { IsCompleted: false } && failure is null && fault is null) return;
            }
            StopCore();
            lock (gate)
            {
                fault = null;
                stopping = false;
                failure = null;
                periodState = null;
                lastLost = 0;
                ledger.Clear();
            }
            worker = Task.Factory.StartNew(Collect, default, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    /// <summary>同步停止并等待唯一消费者完成清理；快速开关不会积累停止任务或与下次启动重叠。</summary>
    public void Stop()
    {
        lock (control) StopCore();
    }

    private void StopCore()
    {
        lock (gate) stopping = true;
        lock (traceGate)
        {
            if (session is { } running)
            {
                try { running.Stop(noThrow: false); }
                catch (Exception error) when (Expected(error)) { Diagnostics.Record("ETW 停止", Diagnostics.Error(error)); }
                catch (Exception error) { UnexpectedFailure(error); }
                // 无论 Stop 的结果如何，都终止本消费者，最终由其 finally 再次释放自己拥有的会话。
                try { running.Source.StopProcessing(); }
                catch (Exception error) when (Expected(error)) { Diagnostics.Record("ETW 消费停止", Diagnostics.Error(error)); }
                catch (Exception error) { UnexpectedFailure(error); }
            }
        }
        worker?.GetAwaiter().GetResult();
        worker = null;
        lock (gate)
        {
            ledger.Clear();
            lastRead = 0;
            periodState = null;
        }
    }

    private void Collect()
    {
        TraceEventSession? trace = null;
        try
        {
            lock (gate) if (stopping) return;
            // 随机实例名 + 禁止接管：不枚举、不停止其他实例或其他软件的会话。
            trace = new TraceEventSession($"SysPulse.Network.{Environment.ProcessId}.{Guid.NewGuid():N}",
                TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate) { StopOnDispose = true };
            trace.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Process);
            var source = trace.Source;
            var kernel = source.Kernel;
            kernel.ProcessStart += Begin;
            // DCStart 的时间是枚举时间而非创建时间，不能据此把旧 PID 的 rundown 绑定给当前实例。
            // 会话前已存在的进程由实时枚举的创建时间补齐。
            kernel.ProcessStop += e =>
            {
                lock (gate) if (!stopping) ledger.End(e.ProcessID, EventQpc(e), e.TimeStamp.ToUniversalTime().Ticks, e.UniqueProcessKey);
            };
            kernel.TcpIpSend += e => Add(e, e.size, upload: true);
            kernel.TcpIpRecv += e => Add(e, e.size, upload: false);
            kernel.TcpIpSendIPV6 += e => Add(e, e.size, upload: true);
            kernel.TcpIpRecvIPV6 += e => Add(e, e.size, upload: false);
            kernel.UdpIpSend += e => Add(e, e.size, upload: true);
            kernel.UdpIpRecv += e => Add(e, e.size, upload: false);
            kernel.UdpIpSendIPV6 += e => Add(e, e.size, upload: true);
            kernel.UdpIpRecvIPV6 += e => Add(e, e.size, upload: false);
            lock (traceGate)
            {
                lock (gate)
                {
                    if (stopping) return;
                    lastRead = Stopwatch.GetTimestamp();
                }
                session = trace;
            }
            Diagnostics.Record("ETW 会话", "运行中；TCP/UDP IPv4/IPv6 与进程生命周期");
            source.Process();
            lock (gate) if (!stopping) failure = "ETW 采集已结束，可点击重试。";
        }
        catch (Exception error) when (Expected(error)) { ExpectedFailure(error); }
        catch (Exception error) { UnexpectedFailure(error); }
        finally
        {
            lock (traceGate)
            {
                session = null;
                try { trace?.Dispose(); }
                catch (Exception error) when (Expected(error)) { ExpectedFailure(error); }
                catch (Exception error) { UnexpectedFailure(error); }
            }
            lock (gate)
            {
                ledger.Clear();
                lastRead = 0;
                Diagnostics.Record("ETW 会话", stopping ? "已停止" : "已结束；等待显式重试");
            }
        }
    }

    /// <summary>Windows 11 的事件原始 QPC 与 Stopwatch 同时间线，避免生命周期边界经浮点毫秒往返舍入。</summary>
    private static long EventQpc(TraceEvent data)
    {
        // TraceEvent 将该属性标为 Discouraged 而非移除；这里专用于跨来源的整数边界比较。
#pragma warning disable CS0618
        return data.TimeStampQPC;
#pragma warning restore CS0618
    }

    private void Begin(ProcessTraceData data)
    {
        if (data.ProcessID <= 0) return;
        long utc = data.TimeStamp.ToUniversalTime().Ticks;
        ProcessIdentity? identity = null;
        try
        {
            using var process = Process.GetProcessById(data.ProcessID);
            identity = ProcessIdentity.Read(process);
            // 迟到的旧 Start 不能绑定已经复用 PID 的新实例。
            if (identity?.CreatedUtcTicks > utc) identity = null;
        }
        catch (Exception error) when (ProcessIdentity.IsUnavailable(error) || error is ArgumentException)
        {
            Diagnostics.Record("ETW 实例身份不可用", Diagnostics.Error(error));
        }
        string name = Path.GetFileNameWithoutExtension(data.KernelImageFileName);
        lock (gate) if (!stopping) ledger.Begin(data.ProcessID, EventQpc(data), utc, data.UniqueProcessKey,
            identity, string.IsNullOrEmpty(name) ? "已退出进程" : name);
    }

    private void Add(TraceEvent data, int bytes, bool upload)
    {
        lock (gate) if (!stopping) ledger.Add(data.ProcessID, EventQpc(data), data.TimeStamp.ToUniversalTime().Ticks, bytes, upload);
    }

    /// <summary>数值枚举确认实例；不在事件热路径逐包访问进程，更不读取路径和图标。</summary>
    internal void Observe(ProcessIdentity identity, string name, long observedQpc)
    {
        lock (gate) if (!stopping && lastRead != 0 && failure is null) ledger.Observe(identity, name, observedQpc);
    }

    /// <summary>取走本周期；实时丢失数必须查询 Session.EventsLost，Source 的快照不用于完整性判定。</summary>
    public TrafficSlice? Take()
    {
        lock (control)
        {
            lock (gate) fault?.Throw();
            try
            {
                lock (traceGate)
                {
                    if (session is null) return null;
                    long lost = unchecked((uint)session.EventsLost);
                    lock (gate)
                    {
                        if (stopping || failure is not null) return null;
                        long now = Stopwatch.GetTimestamp();
                        double seconds = Stopwatch.GetElapsedTime(lastRead, now).TotalSeconds;
                        if (seconds < 0.35) return null;
                        bool reset = lost < lastLost;
                        long delta = LostDelta(lastLost, lost);
                        var slice = ledger.Drain(seconds, now, delta, reset);
                        lastRead = now;
                        lastLost = lost;
                        periodState = reset ? "ETW 本周期不完整：丢失计数回退，已重建归属基线。" :
                            delta > 0 ? $"ETW 本周期不完整：丢失 {delta} 个事件。" :
                            slice.QueueOverflow ? "ETW 本周期不完整：网络事件队列达到上限。" :
                            slice.AttributionLimited ? "ETW 本周期不完整：进程归属缓存达到上限。" :
                            slice.Unattributed != default ? "ETW 本周期含无法可靠归属的字节。" : null;
                        if (periodState is not null) Diagnostics.Record("ETW 完整性", periodState);
                        return slice;
                    }
                }
            }
            catch (Exception error) when (Expected(error))
            {
                ExpectedFailure(error);
                StopCore();
                return null;
            }
        }
    }

    /// <summary>纯函数：非负累计的已知增量；回退的完整性断点另由 LossCounterReset 表示，不伪造丢失数量。</summary>
    internal static long LostDelta(long previous, long current) => current >= previous ? current - previous : current;

    private static bool Expected(Exception error) => error is UnauthorizedAccessException or Win32Exception or
        COMException or IOException or NotSupportedException;

    private void ExpectedFailure(Exception error)
    {
        lock (gate) if (!stopping) failure = "无法启动或读取 ETW：" + Diagnostics.Error(error);
        Diagnostics.Record("ETW 平台故障", Diagnostics.Error(error));
    }

    private void UnexpectedFailure(Exception error)
    {
        lock (gate) fault ??= ExceptionDispatchInfo.Capture(error);
        Diagnostics.Record("ETW 意外故障", Diagnostics.Error(error));
    }

    /// <summary>退出时等待启动／消费／销毁全部结束；不超时遗留仍在运行的系统级会话。</summary>
    public void Dispose()
    {
        lock (control)
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
            }
            StopCore();
        }
    }
}

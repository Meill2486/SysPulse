using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SysPulse;

/// <summary>只在 UI 线程替换的不可变需求；后台绝不读取可变的 Preferences 或窗口控件。</summary>
internal sealed record SamplingOptions(int RefreshMs, Guid? NetworkInterface, bool Overview, bool Processes, int ProcessEpoch = 0, int NetworkEpoch = 0, int OverviewEpoch = 0);
/// <summary>带采样时间的进程快照，重新打开窗口时可明确标识缓存而非当前数据。</summary>
internal sealed record ProcessSnapshot(ProcessRow[] Rows, long Timestamp, string? State);

/// <summary>应用级串行采样生命周期：整机与进程独立，UI 忙时每域只排队一次最新通知。</summary>
internal sealed class MonitorSession
{
    private readonly Dispatcher dispatcher;
    private readonly bool elevated;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim systemWake = new(0, 1), processWake = new(0, 1);
    private readonly object gate = new();
    private readonly Task systemWorker, processWorker;
    private SamplingOptions options;
    private SystemSample latestSystem;
    private readonly TrendHistory history = new();
    private ProcessSnapshot latestProcesses = new([], 0, "正在读取进程…");
    private int systemQueued, processQueued, reset, processReset, closeDetails;
    private int systemFault, processFault, trafficRetry;
    private volatile bool disposed;
    private Task? shutdown;
    internal Appearance Appearance { get; }
    private ProcessTraffic Traffic { get; } = new();
    internal string? TrafficFailure => Traffic.Failure;
    internal string? TrafficPeriodState => Traffic.PeriodState;
    internal bool TrafficRetryPending => Volatile.Read(ref trafficRetry) != 0;
    internal event EventHandler? SystemChanged;
    internal event EventHandler? ProcessesChanged;
    internal SystemSample LatestSystem { get { lock (gate) return latestSystem; } }
    internal ProcessSnapshot LatestProcesses { get { lock (gate) return latestProcesses; } }
    internal TrendPoint[] History { get { lock (gate) return history.Snapshot(); } }

    internal MonitorSession(Dispatcher dispatcher, Preferences preferences, bool elevated)
    {
        this.dispatcher = dispatcher;
        this.elevated = elevated;
        options = new(preferences.RefreshMs, preferences.NetworkInterface, false, false);
        Appearance = new(dispatcher);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        systemWorker = Task.Run(() => RunDomain(SystemLoop, system: true));
        processWorker = Task.Run(() => RunDomain(ProcessLoop, system: false));
    }

    internal void Apply(Preferences preferences)
    {
        var current = Volatile.Read(ref options);
        lock (gate)
            Change(current with { RefreshMs = preferences.RefreshMs, NetworkInterface = preferences.NetworkInterface,
                NetworkEpoch = current.NetworkEpoch + (current.NetworkInterface != preferences.NetworkInterface ? 1 : 0) });
    }
    internal void SetOverview(bool visible)
    {
        var current = Volatile.Read(ref options);
        if (current.Overview == visible || disposed) return;
        if (!visible) Interlocked.Exchange(ref closeDetails, 1);
        lock (gate)
        {
            latestSystem = latestSystem with { DiskRead = null, DiskWrite = null, Gpus = Array.Empty<GpuReading>(), DiskState = "采样中", GpuState = "采样中" };
            Change(current with { Overview = visible, OverviewEpoch = current.OverviewEpoch + 1 });
        }
    }
    internal void SetProcesses(bool visible)
    {
        var current = Volatile.Read(ref options);
        if (current.Processes == visible || disposed) return;
        lock (gate)
        {
            if (visible && Volatile.Read(ref processFault) == 0)
                latestProcesses = latestProcesses with { State = latestProcesses.Rows.Length == 0 ? "正在读取进程…" : "显示上次缓存，正在读取当前数据…" };
            Change(current with { Processes = visible, ProcessEpoch = current.ProcessEpoch + 1 });
        }
    }

    /// <summary>由诊断页恢复故障域；同一工作者重新建立采样资源。</summary>
    internal void RetrySampling()
    {
        if (disposed) return;
        Interlocked.Exchange(ref reset, 1);
        Interlocked.Exchange(ref systemFault, 0);
        Interlocked.Exchange(ref processFault, 0);
        Wake(systemWake);
        Wake(processWake);
    }

    /// <summary>只提交 ETW 重试意图，串行采样线程负责旧会话的收尾和重建。</summary>
    internal void RetryTraffic()
    {
        if (disposed || !elevated || Volatile.Read(ref processFault) != 0 || !Volatile.Read(ref options).Processes) return;
        Interlocked.Exchange(ref trafficRetry, 1);
        Wake(processWake);
    }
    private void Change(SamplingOptions next)
    {
        var previous = Volatile.Read(ref options);
        if (disposed || next == previous) return;
        Volatile.Write(ref options, next);
        if (previous.RefreshMs != next.RefreshMs || previous.NetworkEpoch != next.NetworkEpoch || previous.OverviewEpoch != next.OverviewEpoch) Wake(systemWake);
        if (previous.ProcessEpoch != next.ProcessEpoch) Wake(processWake);
    }

    private void Wake(SemaphoreSlim signal)
    {
        lock (signal) { if (!disposed && signal.CurrentCount == 0) signal.Release(); }
    }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || disposed) return;
        Interlocked.Exchange(ref reset, 1);
        Interlocked.Exchange(ref processReset, 1);
        Wake(systemWake);
        Wake(processWake);
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (disposed) return;
        Interlocked.Exchange(ref reset, 1);
        Wake(systemWake);
    }

    /// <summary>故障时使本域快照失效，等待明确的重试请求；永远只有一个工作者拥有资源。</summary>
    private async Task RunDomain(Func<Task> run, bool system)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try { await run().ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException || !stop.IsCancellationRequested)
                {
                    string state = "采样已停止：" + Diagnostics.Error(error);
                    Diagnostics.Record(system ? "整机故障" : "进程故障", state);
                    lock (gate)
                    {
                        if (system)
                        {
                            history.Clear();
                            latestSystem = new() { CpuState = state, MemoryState = state, NetworkState = state, DiskState = state, GpuState = state };
                            Volatile.Write(ref systemFault, 1);
                        }
                        else
                        {
                            latestProcesses = new([], Stopwatch.GetTimestamp(), state);
                            Volatile.Write(ref trafficRetry, 0);
                            Volatile.Write(ref processFault, 1);
                        }
                    }
                    Notify(system);
                    while ((system ? Volatile.Read(ref systemFault) : Volatile.Read(ref processFault)) != 0)
                        await (system ? systemWake : processWake).WaitAsync(stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private async Task SystemLoop()
    {
        using var sampler = new SystemSampler();
        while (!stop.IsCancellationRequested)
        {
            var current = Volatile.Read(ref options);
            if (Interlocked.Exchange(ref reset, 0) != 0) sampler.Reset();
            if (Interlocked.Exchange(ref closeDetails, 0) != 0) sampler.CloseDetails();
            long started = Stopwatch.GetTimestamp();
            var result = sampler.Read(current.Overview, current.NetworkInterface, current.NetworkEpoch);
            bool published;
            lock (gate)
            {
                var latestOptions = Volatile.Read(ref options);
                published = current.NetworkEpoch == latestOptions.NetworkEpoch && current.OverviewEpoch == latestOptions.OverviewEpoch &&
                    current.RefreshMs == latestOptions.RefreshMs && !stop.IsCancellationRequested;
                if (published)
                {
                    latestSystem = result;
                    history.Add(new(result.Timestamp, result.Cpu, result.Memory, result.Download, result.Upload, result.NetworkSegment, current.RefreshMs));
                }
            }
            if (published)
            {
                Diagnostics.Record("整机采样", $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0}ms; CPU={result.CpuState ?? "有效"}; 网络={result.NetworkState ?? "有效"}");
                Diagnostics.Record("CPU", result.CpuState ?? "有效", result.Cpu.HasValue);
                Diagnostics.Record("内存", result.MemoryState ?? "有效", result.Memory.HasValue);
                Diagnostics.Record("网络", result.NetworkState ?? "有效", result.Download.HasValue);
                if (current.Overview)
                {
                    Diagnostics.Record("GPU", result.GpuState ?? "有效", System.Linq.Enumerable.Any(result.Gpus, gpu => gpu.Usage.HasValue));
                    Diagnostics.Record("系统卷 I/O", result.DiskState ?? "有效", result.DiskRead.HasValue && result.DiskWrite.HasValue);
                }
                Notify(system: true);
            }
            int delay = Math.Max(50, current.RefreshMs - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await systemWake.WaitAsync(delay, stop.Token).ConfigureAwait(false);
        }
    }

    private async Task ProcessLoop()
    {
        bool active = false;
        int activeEpoch = -1;
        using var sampler = new ProcessSampler(Traffic);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var current = Volatile.Read(ref options);
                if (Interlocked.Exchange(ref processReset, 0) != 0 && active)
                {
                    sampler.Reset(); Traffic.Stop(); active = false;
                }
                if (!current.Processes)
                {
                    Interlocked.Exchange(ref trafficRetry, 0);
                    if (active) { sampler.Reset(); Traffic.Stop(); active = false; }
                    await processWake.WaitAsync(stop.Token).ConfigureAwait(false);
                    continue;
                }
                if (!active || activeEpoch != current.ProcessEpoch)
                {
                    sampler.Reset();
                    if (elevated) { if (active) Traffic.Stop(); Traffic.Start(); }
                    active = true;
                    activeEpoch = current.ProcessEpoch;
                }
                if (Volatile.Read(ref trafficRetry) != 0)
                {
                    Traffic.Start();
                    Volatile.Write(ref trafficRetry, 0);
                }
                long started = Stopwatch.GetTimestamp();
                var rows = sampler.Read();
                bool published;
                lock (gate)
                {
                    published = Volatile.Read(ref options) is { Processes: true } latest && latest.ProcessEpoch == activeEpoch && !stop.IsCancellationRequested;
                    if (published) latestProcesses = new(rows, Stopwatch.GetTimestamp(), null);
                }
                if (published)
                {
                    Diagnostics.Record("进程采样", $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0}ms; 应用组={rows.Length}");
                    Notify(system: false);
                }
                await processWake.WaitAsync(Math.Max(50, 1000 - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds), stop.Token).ConfigureAwait(false);
            }
        }
        finally { Traffic.Stop(); }
    }

    private void Notify(bool system)
    {
        if (stop.IsCancellationRequested || dispatcher.HasShutdownStarted) return;
        if (system ? Interlocked.Exchange(ref systemQueued, 1) != 0 : Interlocked.Exchange(ref processQueued, 1) != 0) return;
        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (system) Interlocked.Exchange(ref systemQueued, 0); else Interlocked.Exchange(ref processQueued, 0);
            if (disposed) return;
            if (system) SystemChanged?.Invoke(this, EventArgs.Empty); else ProcessesChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>在 UI 线程发起退出，原工作者释放各自资源，调用方可异步等待。</summary>
    internal Task StopAsync()
    {
        if (shutdown is not null) return shutdown;
        disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        Appearance.Dispose();
        stop.Cancel();
        return shutdown = FinishStop();
    }

    private async Task FinishStop()
    {
        await Task.WhenAll(systemWorker, processWorker).ConfigureAwait(false);
        Traffic.Dispose();
        // 系统事件回调可能已在 Wake 内，沿用同一把锁后再释放信号量。
        lock (systemWake) systemWake.Dispose();
        lock (processWake) processWake.Dispose();
        stop.Dispose();
    }
}

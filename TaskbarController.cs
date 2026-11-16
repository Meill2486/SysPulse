using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SysPulse;

/// <summary>停靠状态由显示意图、全屏、安全空位和交互条件决定；全屏始终优先。</summary>
internal enum DockState { Unavailable, UserHidden, FullScreen, NoSafeSpace, Docked, Menu, Dragging }

/// <summary>主屏任务栏的事件合并、低频校验与安全几何缓存；UIA 仅由专用 MTA 线程管理。</summary>
internal sealed class TaskbarController : IDisposable
{
    private sealed record Probe(TaskbarFrame? Frame, int Generation, int Connection);
    private readonly Dispatcher dispatcher;
    private readonly Func<bool> requested;
    private readonly DispatcherTimer health = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer coalesce = new() { Interval = TimeSpan.FromMilliseconds(75) };
    private readonly TaskbarDock.LayerEvents layerEvents;
    private readonly AutoResetEvent wake = new(false);
    private readonly object gate = new();
    private readonly Thread automationThread;
    private Probe? pending;
    private int generation, connection, queued;
    private bool stopping, disposed, dirty = true;
    private long lastRequest, lastSuccess;
    private int? occupied;
    private DockState state;
    internal TaskbarFrame? Frame { get; private set; }
    internal System.Drawing.Rectangle? Slot => Frame is { } frame && occupied is { } end &&
        Stopwatch.GetElapsedTime(lastSuccess).TotalSeconds <= 5 ? TaskbarDock.FreeSlot(frame, end) : null;
    internal event EventHandler? Changed;
    internal event EventHandler? Heartbeat;

    internal TaskbarController(Dispatcher dispatcher, Func<bool> requested)
    {
        this.dispatcher = dispatcher;
        this.requested = requested;
        automationThread = new Thread(AutomationLoop) { IsBackground = true, Name = "SysPulse.Taskbar.UIA" };
        automationThread.SetApartmentState(ApartmentState.MTA);
        automationThread.Start();
        layerEvents = new TaskbarDock.LayerEvents(layout => Queue(layout));
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        health.Tick += (_, _) => { Refresh(); Heartbeat?.Invoke(this, EventArgs.Empty); };
        coalesce.Tick += (_, _) => { coalesce.Stop(); Refresh(); };
        health.Start();
    }

    internal void SetState(DockState next)
    {
        if (state == next) return;
        state = next;
        Diagnostics.Record("任务栏状态", next.ToString());
    }

    /// <summary>事件只投递一个 UI 工作项，75ms 内的布局风暴合并为一次查询。</summary>
    internal void Invalidate(bool reconnect = false)
    {
        if (reconnect)
        {
            Interlocked.Increment(ref connection);
            Interlocked.Exchange(ref geometryDirty, 1);
        }
        Queue(true);
    }
    private void DisplayChanged(object? sender, EventArgs e) => Queue(true);
    private void Queue(bool layout)
    {
        if (disposed || dispatcher.HasShutdownStarted) return;
        if (layout) Interlocked.Exchange(ref layoutDirty, 1);
        if (Interlocked.Exchange(ref queued, 1) != 0) return;
        dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            Interlocked.Exchange(ref queued, 0);
            if (disposed) return;
            coalesce.Start();
        });
    }
    private int layoutDirty, geometryDirty;

    /// <summary>与事件使用同一读取算法；稳态 UIA 不高于每 2 秒一次。</summary>
    internal void Refresh()
    {
        if (disposed) return;
        var next = requested() ? TaskbarDock.ReadFrame() : null;
        bool changed = next != Frame;
        bool layoutChanged = next is { } a && Frame is { } b && !a.SameLayout(b);
        if (changed)
        {
            generation++;
            if (layoutChanged || next is null || Frame is null) occupied = null;
            Frame = next;
            dirty = true;
        }
        if (Interlocked.Exchange(ref layoutDirty, 0) != 0)
        {
            generation++;
            // 控件动画只是重查提示，不先隐藏；只有 Explorer 换代必须丢弃旧几何。
            if (Interlocked.Exchange(ref geometryDirty, 0) != 0) occupied = null;
            dirty = true;
        }
        if (next is { } frame && !frame.Tray.IsEmpty)
        {
            if (dirty || lastRequest == 0 || Stopwatch.GetElapsedTime(lastRequest).TotalSeconds >= 2)
            {
                lastRequest = Stopwatch.GetTimestamp();
                Request(new(frame, generation, Volatile.Read(ref connection)));
                dirty = false;
            }
        }
        else if (changed) Request(new(null, generation, Volatile.Read(ref connection)));
        if (!requested()) SetState(DockState.UserHidden);
        else if (next is null) SetState(TaskbarDock.HasForegroundFullScreen() ? DockState.FullScreen : DockState.Unavailable);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Request(Probe probe)
    {
        lock (gate) { if (stopping) return; pending = probe; wake.Set(); }
    }

    /// <summary>只保留一个待处理请求；阻塞的 COM 调用不假装取消，也不另开替代查询线程。</summary>
    private void AutomationLoop()
    {
        int rootGeneration = -1;
        AutomationElement? root = null;
        nint rootHandle = 0;
        long failedAt = 0;
        int failedConnection = -1;
        StructureChangedEventHandler structure = (_, _) => { Diagnostics.Record("UIA 结构事件", "任务栏内容变化"); Queue(true); };
        void Detach()
        {
            if (root is null) return;
            var previous = root;
            root = null;
            rootHandle = 0;
            Diagnostics.Record("UIA 连接", "退订结构事件");
            try
            {
                Automation.RemoveStructureChangedEventHandler(previous, structure);
                Diagnostics.Record("UIA 连接", "已退订");
            }
            catch (Exception error) { Diagnostics.Record("UIA 退订", Diagnostics.Error(error)); }
        }
        try
        {
            while (true)
            {
                wake.WaitOne();
                Probe? work;
                lock (gate) { if (stopping) break; work = pending; pending = null; }
                if (work is null) continue;
                if (work.Frame is not { } frame) continue; // 普通隐藏/全屏不销毁同一 Explorer 的订阅。
                if (failedConnection == work.Connection && Stopwatch.GetElapsedTime(failedAt).TotalSeconds < 10) continue;
                int? result = null;
                long started = Stopwatch.GetTimestamp();
                try
                {
                    if (root is null || rootHandle != frame.Handle || rootGeneration != work.Connection)
                    {
                        Detach();
                        Diagnostics.Record("UIA 连接", "获取任务栏内容");
                        // owned 顶层窗口也会出现在 Shell 的 UIA 子树中；只订阅 Explorer 的实际按钮区域。
                        root = AutomationElement.FromHandle(frame.Handle).FindFirst(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.AutomationIdProperty, "TaskbarFrame"));
                        if (root is null) throw new ElementNotAvailableException("无法确认任务栏内容区域");
                        rootHandle = frame.Handle;
                        rootGeneration = work.Connection;
                        Diagnostics.Record("UIA 连接", "订阅任务栏内容");
                        // 结构事件配合原生位置事件与低频复核即可；不保留本机退订会阻塞的属性订阅。
                        Automation.AddStructureChangedEventHandler(root, TreeScope.Subtree, structure);
                        Diagnostics.Record("UIA 连接", "已连接");
                    }
                    result = TaskbarDock.ReadOccupiedEnd(frame, root);
                    if (!result.HasValue) Detach();
                }
                catch (Exception error) when (ExpectedAutomationError(error))
                {
                    Diagnostics.Record("UIA 错误", Diagnostics.Error(error));
                    Detach();
                }
                catch (Exception error)
                {
                    // 意外查询失败后退订并限频重连；用户发起重新初始化可立即跳过等待。
                    Diagnostics.Record("UIA 查询故障", Diagnostics.Error(error));
                    failedAt = Stopwatch.GetTimestamp();
                    failedConnection = work.Connection;
                    Detach();
                }
                Diagnostics.Record("UIA 查询", $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0}ms; {(result.HasValue ? "已确认" : "未确认")}");
                if (dispatcher.HasShutdownStarted || disposed) continue;
                dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
                {
                    if (disposed || work.Generation != generation || Frame != work.Frame) return;
                    // 暂时读不到时仍受五秒有效期约束，不把动画中的失败扩散为显隐循环。
                    if (result.HasValue)
                    {
                        occupied = result;
                        lastSuccess = Stopwatch.GetTimestamp();
                    }
                    Changed?.Invoke(this, EventArgs.Empty);
                });
            }
        }
        catch (Exception error)
        {
            // 消息循环本身失效时，缓存仍会过期收起。
            Diagnostics.Record("UIA 域停止", Diagnostics.Error(error));
        }
        finally { Detach(); lock (gate) { stopping = true; wake.Dispose(); } }
    }

    private static bool ExpectedAutomationError(Exception error) => error is ElementNotAvailableException or
        InvalidOperationException or COMException or UnauthorizedAccessException;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        health.Stop(); coalesce.Stop();
        layerEvents.Dispose();
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        lock (gate) { if (!stopping) { stopping = true; wake.Set(); } }
        if (!automationThread.Join(500)) Diagnostics.Record("UIA 退出", "正在等待在途系统调用返回后退订；未另建线程");
    }
}

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Rectangle = System.Drawing.Rectangle;

namespace SysPulse;

/// <summary>
/// 显示常驻任务栏的整机快照，处理贴靠和点击交互，不持有采样定时器。
/// </summary>
public partial class MonitorBar : Window
{
    private readonly MonitorSession session;
    private readonly Preferences settings;
    private readonly HwndSourceHook pointerHook;
    private TaskbarController? controller;
    private TaskbarFrame? probedFrame => controller?.Frame;
    private Size? desiredSize;
    private double desiredScale;
    private double? ringValue;
    private Rectangle? slot;
    private Rectangle? placedBounds;
    private bool contrasting;
    private bool trayMenuOpen;
    private bool barMenuOpen;
    private bool? sampledLight;
    private bool? capsuleLight;
    private bool closed;
    private OverviewWindow? overview;
    private PopupDismissal? popupDismissal;
    private (int Pointer, int Origin)? dragStart;
    private int? dragPosition;
    private bool dragFramePending;
    private bool pressedNetwork;
    private nint handle;
    private SystemSample sample;

    /// <summary>
    /// 用户保存的显示意图。任务栏条件会决定监控条此刻是否实际可见。
    /// </summary>
    internal bool RequestedVisible => settings.BarVisible;
    internal event EventHandler? ExitRequested;
    internal event EventHandler? SettingsRequested;
    internal event EventHandler? ProcessesRequested;
    internal event EventHandler? PreferencesChanged;

    /// <summary>
    /// 初始化监控条的控件和调度器。实际监听由启动入口开启。
    /// </summary>
    internal MonitorBar(Preferences settings, MonitorSession session)
    {
        this.settings = settings;
        this.session = session;
        session.SystemChanged += ReceiveSample;
        session.Appearance.Changed += OnAppearanceChanged;
        pointerHook = HandlePointerMessage;
        InitializeComponent();
        Closed += (_, _) =>
        {
            closed = true;
            controller?.Dispose();
            ResetDrag();
            session.SystemChanged -= ReceiveSample;
            session.Appearance.Changed -= OnAppearanceChanged;
            session.SetOverview(false);
            popupDismissal?.Dispose();
        };
    }

    /// <summary>
    /// 创建非激活窗口并开启位置、采样和对比度监测。
    /// </summary>
    internal void Start()
    {
        handle = new WindowInteropHelper(this).EnsureHandle();
        TaskbarDock.MakeNonActivating(handle);
        HwndSource.FromHwnd(handle).AddHook(pointerHook);
        controller = new TaskbarController(Dispatcher, () => RequestedVisible &&
            (settings.ShowDownload || settings.ShowUpload || settings.ShowCpu || settings.ShowMemory));
        controller.Changed += (_, _) => RefreshPlacement();
        controller.Heartbeat += async (_, _) => { ExpireSample(); await RefreshContrast(); };
        ApplyPreferences();
        ReceiveSample(this, EventArgs.Empty);
    }

    /// <summary>
    /// 切换用户的显示意图，即时应用并通知配置保存。
    /// </summary>
    internal void ToggleRequestedVisibility()
    {
        settings.BarVisible = !settings.BarVisible;
        NotifyPreferencesChanged();
    }

    /// <summary>
    /// 应用当前设置并重新评估监控条尺寸和安全位置。
    /// </summary>
    internal void ApplyPreferences()
    {
        session.Apply(settings);
        CapsuleSurface.Opacity = settings.BarOpacity / 100d;
        ApplyLayout();
        RenderSample();
        desiredSize = null;
        controller?.Refresh();
    }

    private void NotifyPreferencesChanged()
    {
        ApplyPreferences();
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 记录托盘菜单状态。菜单关闭后恢复正常贴靠检查。
    /// </summary>
    internal void SetTrayMenuOpen(bool open)
    {
        trayMenuOpen = open;
        if (!open)
            RefreshPlacement();
    }

    private void QueuePlacement() => controller?.Invalidate();

    /// <summary>诊断页请求重新连接 Explorer 并作废旧安全空位。</summary>
    internal void RetryTaskbar() => controller?.Invalidate(reconnect: true);

    /// <summary>
    /// 在有效任务栏边界内定位监控条，并在安全空间不足时暂时收起。
    /// </summary>
    private void RefreshPlacement()
    {
        if (closed || controller is null) return;
        if (barMenuOpen || trayMenuOpen)
        {
            controller.SetState(DockState.Menu);
            RestoreMenuLayer();
            return;
        }
        if (probedFrame is not { } current)
        {
            HideBar();
            return;
        }
        if (controller.Slot is not { } free || free.IsEmpty)
        {
            controller.SetState(DockState.NoSafeSpace);
            HideBar();
            return;
        }
        slot = free;
        if (desiredSize is null || desiredScale != current.Scale)
        {
            // 显隐、标签等变化可能只使子控件的测量失效。先完成 WPF 待处理的布局，
            // 再测量整条的自然尺寸，避免把旧宽度传给原生窗口后触发 Viewbox 的全条缩放。
            Capsule.UpdateLayout();
            Capsule.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            desiredSize = Capsule.DesiredSize;
            desiredScale = current.Scale;
            Diagnostics.Record("监控条测量", "设置或 DPI 改变");
        }
        var desired = desiredSize.Value;
        double contentScale = Math.Min(current.Scale, free.Height / Math.Max(1, desired.Height));
        int length = (int)Math.Ceiling(desired.Width * contentScale);
        int last = free.Right - length;
        if (length == 0 || last < free.Left)
        {
            controller.SetState(DockState.NoSafeSpace);
            HideBar();
            return;
        }
        double target = dragPosition ?? (settings.BarOffset == -1 ? last : current.Bounds.Left + settings.BarOffset * current.Scale);
        int position = (int)Math.Clamp(target, free.Left, last);
        var bounds = new Rectangle(position, free.Top, length, free.Height);
        // 顶层所有者保证监控条随任务栏提升且始终在其上方，不靠点击后的抢层补位。
        var interop = new WindowInteropHelper(this);
        if (interop.Owner != current.Handle) interop.Owner = current.Handle;
        placedBounds = bounds;
        TaskbarDock.Place(handle, bounds, current.Handle);
        if (!IsVisible)
        {
            Show();
            TaskbarDock.Place(handle, bounds, current.Handle);
        }
        controller.SetState(dragPosition.HasValue ? DockState.Dragging : DockState.Docked);
        if (overview?.IsVisible == true) overview.Anchor(current, bounds);
    }

    private void HideBar()
    {
        CancelDrag();
        CloseOverview();
        if (IsVisible)
            Hide();
    }


    /// <summary>采样域已合并过通知，UI 只消费当前最新快照。</summary>
    private void ReceiveSample(object? sender, EventArgs e)
    {
        if (closed) return;
        sample = session.LatestSystem;
        RenderSample();
    }

    private void OnAppearanceChanged(object? sender, EventArgs e) => RenderSample();

    /// <summary>在途读取异常变慢时明确过期，不让最后一次有效值无限冒充实时数据。</summary>
    private void ExpireSample()
    {
        if (sample.Timestamp == 0 || sample.CpuState?.StartsWith("样本已过期", StringComparison.Ordinal) == true ||
            Stopwatch.GetElapsedTime(sample.Timestamp).TotalMilliseconds <= Math.Max(3000, settings.RefreshMs * 2.5)) return;
        string state = $"样本已过期（最后更新 {sample.CapturedAt:T}）";
        sample = sample with { Cpu = null, Memory = null, Download = null, Upload = null, MemoryUsed = 0, MemoryTotal = 0,
            DiskRead = null, DiskWrite = null, Gpus = Array.Empty<GpuReading>(), CpuState = state, MemoryState = state,
            NetworkState = state, DiskState = state, GpuState = state };
        RenderSample();
    }

    private void ApplyLayout()
    {
        bool line = settings.BarStyle != BarStyle.Stack;
        bool ring = settings.BarStyle == BarStyle.Ring;
        bool network = settings.IsMetricEnabled(BarMetric.Network);
        int font = settings.FontSize;
        Metrics.Orientation = Orientation.Horizontal;
        // 有背景时给文字、圆环和描边留出呼吸空间；透明模式仍使用原来的紧凑边界。
        bool hasBackground = settings.BarOpacity > 0;
        Metrics.Margin = hasBackground ? new Thickness(10, 2, 10, 2) : new Thickness(7, 0, 5, 0);
        Capsule.Margin = hasBackground ? new Thickness(0, 2, 0, 2) : new Thickness(0);
        Capsule.MinHeight = 32;
        MemoryGroup.Visibility = VisibleIf(settings.ShowMemory);
        MemoryRing.Visibility = VisibleIf(ring);
        RingGraphic.Width = 34 + (font - 12) * 2;
        RingGraphic.Height = 36 + (font - 12);
        RingText.FontSize = Math.Clamp(font - 2, 9, 12);
        RingLabel.Text = settings.MemLabel;
        RingLabel.FontSize = font - 2;
        RingLabel.Visibility = VisibleIf(settings.MemLabel.Length != 0);
        NetworkBlock.Visibility = VisibleIf(network);
        NetworkBlock.Margin = new Thickness(0);
        DownloadLine.Visibility = VisibleIf(settings.ShowDownload);
        UploadLine.Visibility = VisibleIf(settings.ShowUpload);
        DownloadText.FontSize = UploadText.FontSize = font - 1;
        double rateWidth = 84 * (font - 1) / 11d;
        DownloadIcon.Width = DownloadIcon.Height = UploadIcon.Width = UploadIcon.Height = 11;
        // 每行按实际内容居中，组宽固定以免网速变化时推动其他指标。
        DownloadText.MaxWidth = UploadText.MaxWidth = rateWidth;
        NetworkBlock.Width = rateWidth + DownloadIcon.Width + DownloadIcon.Margin.Right;

        CpuGroup.Visibility = VisibleIf(settings.ShowCpu);
        SetMetricLayout(CpuBlock, CpuText, CpuLabel, settings.CpuLabel, true, line, font);
        SetMetricLayout(MemoryBlock, MemoryText, MemoryLabel, settings.MemLabel, !ring, line, font);
        PlaceChild(CpuBlock, CpuCaption, line ? 0 : 1);
        PlaceChild(MemoryBlock, MemoryCaption, line ? 0 : 1);
        ApplyIcons();
        ApplyMetricOrder();
    }

    /// <summary>
    /// 根据监控条专用设置选择内置图标。环形和纯文字布局共用同一个内存图标选项。
    /// </summary>
    private void ApplyIcons()
    {
        string? cpu = settings.CpuIcon switch
        {
            CpuIconStyle.Chip => "IconCpu",
            CpuIconStyle.Pulse => "IconActivity",
            _ => null
        };
        string? memory = settings.MemoryIcon switch
        {
            MemoryIconStyle.Module => "IconMemory",
            MemoryIconStyle.Layers => "IconMemoryLayers",
            _ => null
        };
        CpuIcon.Visibility = VisibleIf(cpu is not null);
        CpuIcon.Data = cpu is null ? null : (Geometry)FindResource(cpu);
        MemoryIcon.Visibility = RingIcon.Visibility = VisibleIf(memory is not null);
        MemoryIcon.Data = RingIcon.Data = memory is null ? null : (Geometry)FindResource(memory);

        var (down, up) = settings.NetworkSymbols switch
        {
            NetworkSymbolStyle.Chevron => ("IconChevronDownload", "IconChevronUpload"),
            NetworkSymbolStyle.Triangle => ("IconTriangleDownload", "IconTriangleUpload"),
            _ => ("IconDownload", "IconUpload")
        };
        DownloadIcon.Data = (Geometry)FindResource(down);
        UploadIcon.Data = (Geometry)FindResource(up);
    }

    /// <summary>
    /// 按用户配置重新排列现有控件，并根据显示项调整组间分隔。
    /// </summary>
    private void ApplyMetricOrder()
    {
        // 只调整原有控件的顺序，网速区的点击位置随控件一起移动。
        FrameworkElement Group(BarMetric metric) => metric switch
        {
            BarMetric.Network => NetworkBlock,
            BarMetric.Cpu => CpuGroup,
            _ => MemoryGroup
        };
        var order = settings.GetMetricOrder();
        var first = Group(order.First);
        var second = Group(order.Second);
        var third = Group(order.Third);
        PlaceChild(Metrics, first, 0);
        PlaceChild(Metrics, FirstDivider, 1);
        PlaceChild(Metrics, second, 2);
        PlaceChild(Metrics, SecondDivider, 3);
        PlaceChild(Metrics, third, 4);

        // 隐藏一组时仅在剩下的相邻指标之间保留分隔线。
        bool secondShown = second.Visibility == Visibility.Visible;
        bool thirdShown = third.Visibility == Visibility.Visible;
        FirstDivider.Visibility = VisibleIf(first.Visibility == Visibility.Visible && (secondShown || thirdShown));
        SecondDivider.Visibility = VisibleIf(secondShown && thirdShown);
    }

    private static void PlaceChild(Panel parent, UIElement child, int index)
    {
        if (parent.Children.IndexOf(child) == index)
            return;
        parent.Children.Remove(child);
        parent.Children.Insert(index, child);
    }

    private static void SetMetricLayout(StackPanel block, TextBlock value, TextBlock label,
        string text, bool visible, bool line, int font)
    {
        block.Visibility = VisibleIf(visible);
        block.Orientation = line ? Orientation.Horizontal : Orientation.Vertical;
        value.Width = font * 3;
        value.FontSize = font;
        value.VerticalAlignment = VerticalAlignment.Center;
        label.Text = text;
        label.Visibility = VisibleIf(text.Length != 0);
        label.FontSize = font - 2;
        label.VerticalAlignment = VerticalAlignment.Center;
    }

    private static Visibility VisibleIf(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    private bool IsLight(bool systemLight) => settings.Theme == ColorTheme.Light ||
        (settings.Theme == ColorTheme.System && systemLight);

    private void RenderSample()
    {
        bool light = IsLight((settings.BarOpacity == 0 ? sampledLight : null) ?? session.Appearance.BarLight);
        var colors = light ? MetricDisplay.Light : MetricDisplay.Dark;
        if (capsuleLight != light)
        {
            capsuleLight = light;
            CapsuleSurface.Background = (Brush)FindResource(light ? "LightCapsuleFill" : "DarkCapsuleFill");
            CapsuleSurface.BorderBrush = (Brush)FindResource(light ? "LightCapsuleEdge" : "DarkCapsuleEdge");
        }
        CpuText.Text = MetricDisplay.Percent(sample.Cpu);
        CpuText.Foreground = colors.Status(sample.Cpu, settings.CpuWarn, settings.CpuAlert);
        CpuIcon.Foreground = MemoryIcon.Foreground = RingIcon.Foreground = colors.Foreground;
        FirstDivider.Background = SecondDivider.Background = colors.Track;
        CpuLabel.Foreground = MemoryLabel.Foreground = RingLabel.Foreground = colors.Foreground;
        MemoryText.Text = RingText.Text = MetricDisplay.Percent(sample.Memory);
        MemoryText.Foreground = RingText.Foreground = MemoryArc.Stroke = colors.Status(sample.Memory, settings.MemWarn, settings.MemAlert);
        MemoryTrack.Stroke = colors.Track;
        if (sample.Memory != ringValue) { ringValue = sample.Memory; MemoryArc.Data = MemoryProgress(ringValue); }
        DownloadText.Text = MetricDisplay.Bytes(sample.Download, rate: true);
        UploadText.Text = MetricDisplay.Bytes(sample.Upload, rate: true);
        CpuText.ToolTip = sample.CpuState;
        MemoryText.ToolTip = RingText.ToolTip = sample.MemoryState;
        NetworkBlock.ToolTip = sample.NetworkState;
        (DownloadText.Foreground, UploadText.Foreground) = colors.Network(sample, settings);
        DownloadIcon.Foreground = colors.Normal;
        UploadIcon.Foreground = colors.Upload;
        if (overview?.IsVisible == true)
            overview.Render(sample, settings, IsLight(session.Appearance.AppsLight), session.History);
    }

    private static Geometry? MemoryProgress(double? percent)
    {
        if (percent is null or <= 0)
            return null;

        const double center = 17;
        const double radius = 14;
        if (percent >= 100)
            return new EllipseGeometry(new Point(center, center), radius, radius);

        double angle = percent.Value / 100 * Math.Tau;
        var end = new Point(center + Math.Sin(angle) * radius, center - Math.Cos(angle) * radius);
        var arc = new StreamGeometry();
        using (var path = arc.Open())
        {
            path.BeginFigure(new Point(center, center - radius), false, false);
            path.ArcTo(end, new Size(radius, radius), 0, percent > 50, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        return arc;
    }

    /// <summary>
    /// 后台读取相邻任务栏亮度，串行采样并丢弃过期位置的结果，避免阻塞所有窗口的输入。
    /// </summary>
    private async Task RefreshContrast()
    {
        if (contrasting || closed || settings.BarOpacity != 0 || dragPosition.HasValue || settings.Theme != ColorTheme.System || !IsVisible ||
            barMenuOpen || trayMenuOpen || probedFrame is not { } frame ||
            placedBounds is not { } bounds || slot is not { } free)
            return;

        contrasting = true;
        try
        {
            // 桌面 GetPixel 可能等待显卡同步；只把复制的几何数据交给后台线程。
            double? luminance = await Task.Run(() => TaskbarDock.AdjacentBrightness(bounds, free));
            if (closed || !IsVisible || dragPosition.HasValue || settings.Theme != ColorTheme.System ||
                barMenuOpen || trayMenuOpen || probedFrame != frame || placedBounds != bounds || slot != free)
                return;

            // 滞回区内保持当前颜色，避免任务栏底色小幅波动时反复切换。
            bool? next = luminance switch
            {
                >= 0.50 => true,
                <= 0.40 => false,
                null => null,
                _ => sampledLight ?? session.Appearance.BarLight
            };
            if (sampledLight != next)
            {
                sampledLight = next;
                RenderSample();
            }
        }
        finally { contrasting = false; }
    }

    /// <summary>
    /// 用窗口客户区坐标处理左键命中、拖动与捕获变化。
    /// </summary>
    private nint HandlePointerMessage(nint window, int message, nint buttons, nint position, ref bool handled)
    {
        if (message is 0x007E or 0x02E0 or 0x001A || message == TaskbarDock.TaskbarCreatedMessage)
        {
            if (message == TaskbarDock.TaskbarCreatedMessage)
                new WindowInteropHelper(this).Owner = 0; // Explorer 换代时解除旧关联，即使 HWND 数值复用也重新绑定。
            desiredSize = null;
            controller?.Invalidate(reconnect: message == TaskbarDock.TaskbarCreatedMessage);
        }
        if (placedBounds is not { } bounds)
            return 0;

        int coordinate = bounds.Left + (short)(long)position;
        switch (message)
        {
            case 0x0201: // WM_LBUTTONDOWN
                dragStart = (coordinate, bounds.Left);
                pressedNetwork = IsNetworkCoordinate(coordinate);
                handled = true;
                break;
            case 0x0200 when ((long)buttons & 1) != 0: // WM_MOUSEMOVE | MK_LBUTTON
                ContinueDrag(coordinate);
                handled = dragPosition.HasValue;
                break;
            case 0x0202: // WM_LBUTTONUP
                EndDrag(coordinate);
                handled = true;
                break;
            case 0x0215: // WM_CAPTURECHANGED
            case 0x001F: // WM_CANCELMODE
                CancelDrag();
                break;
            case 0x02A3 when !TaskbarDock.HasPointerCapture(window): // WM_MOUSELEAVE
                CancelDrag();
                break;
        }
        return 0;
    }

    private void ContinueDrag(int coordinate)
    {
        if (dragStart is not { } start || probedFrame is not { } frame)
            return;

        int delta = coordinate - start.Pointer;
        if (!dragPosition.HasValue)
        {
            if (Math.Abs(delta) < 5 * frame.Scale)
                return;
            TaskbarDock.CapturePointer(handle);
            CloseOverview();
        }

        // 高频鼠标消息只更新目标位置，任务栏边界由常规贴靠检查更新。
        int target = start.Origin + delta;
        if (dragPosition == target)
            return;
        dragPosition = target;
        if (!dragFramePending)
        {
            dragFramePending = true;
            CompositionTarget.Rendering += MoveOnRender;
        }
    }

    private void MoveOnRender(object? sender, EventArgs e)
    {
        ClearDragFrame();
        MoveDrag();
    }

    /// <summary>
    /// 合并连续鼠标消息，在最近确认的安全区域内只移动窗口位置。
    /// </summary>
    private void MoveDrag()
    {
        if (dragPosition is not { } target || slot is not { } free || placedBounds is not { } bounds)
            return;

        bounds.X = Math.Clamp(target, free.Left, free.Right - bounds.Width);
        if (bounds == placedBounds)
            return;

        placedBounds = bounds;
        TaskbarDock.Move(handle, bounds.Location);
    }

    private void EndDrag(int coordinate)
    {
        if (dragStart is not { } start)
            return;

        bool dragged = dragPosition.HasValue;
        if (dragged)
        {
            // 松手时先应用最后的坐标，避免把上一帧的位置写入配置。
            dragPosition = start.Origin + coordinate - start.Pointer;
            MoveDrag();
        }
        ResetDrag();
        if (dragged && probedFrame is { } frame && slot is { } free && placedBounds is { } bounds)
        {
            int trayGap = free.Right - bounds.Right;
            int offset = bounds.Left - frame.Bounds.Left;
            settings.BarOffset = trayGap <= 24 * frame.Scale ? -1 : (int)Math.Round(offset / frame.Scale);
            NotifyPreferencesChanged();
        }
        else if (!dragged)
        {
            if (pressedNetwork)
                ProcessesRequested?.Invoke(this, EventArgs.Empty);
            else
                ToggleOverview();
        }
    }

    private bool IsNetworkCoordinate(int coordinate)
    {
        if (!NetworkBlock.IsVisible || NetworkBlock.ActualWidth <= 0 || NetworkBlock.ActualHeight <= 0)
            return false;
        var start = NetworkBlock.PointToScreen(new Point());
        var end = NetworkBlock.PointToScreen(new Point(NetworkBlock.ActualWidth, NetworkBlock.ActualHeight));
        return coordinate >= start.X && coordinate < end.X;
    }

    /// <summary>
    /// 提供当前可见监控条的任务栏和边界，用于进程明细贴靠。
    /// </summary>
    internal (TaskbarFrame? Frame, Rectangle? Bounds) ProcessAnchor =>
        IsVisible ? (probedFrame, placedBounds) : (null, null);

    private void CancelDrag()
    {
        if (dragStart is null)
            return;
        ResetDrag();
        QueuePlacement();
    }

    private void ResetDrag()
    {
        dragStart = null;
        dragPosition = null;
        ClearDragFrame();
        if (TaskbarDock.HasPointerCapture(handle))
            TaskbarDock.ReleasePointer();
    }

    private void ClearDragFrame()
    {
        if (!dragFramePending)
            return;
        CompositionTarget.Rendering -= MoveOnRender;
        dragFramePending = false;
    }

    private void ToggleOverview()
    {
        if (overview?.IsVisible == true)
        {
            CloseOverview();
            return;
        }
        if (probedFrame is not { } frame || placedBounds is not { } bounds)
            return;

        if (overview is null)
        {
            overview = new OverviewWindow();
            overview.SettingsRequested += (_, _) => RequestSettings();
            overview.ProcessesRequested += (_, _) => ProcessesRequested?.Invoke(this, EventArgs.Empty);
        }
        _ = overview.RefreshNetworkSource(settings.NetworkInterface);
        overview.Render(sample with { DiskRead = null, DiskWrite = null, Gpus = Array.Empty<GpuReading>(), GpuState = "采样中", DiskState = "采样中" },
            settings, IsLight(session.Appearance.AppsLight), session.History);
        overview.Anchor(frame, bounds);
        popupDismissal = new PopupDismissal(
            (x, y) =>
            {
                if (placedBounds?.Contains(x, y) != true && overview?.ScreenBounds.Contains(x, y) != true)
                    CloseOverview();
            },
            CloseOverview);
        session.SetOverview(true);
    }

    /// <summary>
    /// 隐藏概览并卸载本次打开时注册的输入监听。
    /// </summary>
    internal void CloseOverview()
    {
        popupDismissal?.Dispose();
        session.SetOverview(false);
        popupDismissal = null;
        if (overview?.IsVisible == true)
            overview.Hide();
    }

    private void RequestSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OpenBarMenu(object sender, MouseButtonEventArgs e)
    {
        if (probedFrame is not { } frame || barMenuOpen)
            return;

        e.Handled = true;
        CloseOverview();
        var cursor = PointToScreen(e.GetPosition(this));
        barMenuOpen = true;
        uint selection;
        try
        {
            selection = TaskbarDock.ShowBarMenu(handle, frame, (int)cursor.X, (int)cursor.Y, RestoreMenuLayer);
        }
        finally
        {
            barMenuOpen = false;
        }

        switch (selection)
        {
            case 1:
                ToggleRequestedVisibility();
                break;
            case 2:
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
            case 3:
                RequestSettings();
                break;
            case 4:
                settings.BarOffset = -1;
                NotifyPreferencesChanged();
                break;
            default:
                RefreshPlacement();
                break;
        }
    }

    /// <summary>菜单与托盘动画期间按原位补位；全屏让位优先于菜单保留。</summary>
    private void RestoreMenuLayer()
    {
        if (TaskbarDock.HasForegroundFullScreen())
        {
            HideBar();
            return;
        }
        if (IsVisible && probedFrame is { } frame && placedBounds is { } bounds)
            TaskbarDock.Place(handle, bounds, frame.Handle);
    }
}

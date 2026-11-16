using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Rectangle = System.Drawing.Rectangle;

namespace SysPulse;

/// <summary>
/// 显示整机快照、网络累计和可用的硬件规格。
/// </summary>
public partial class OverviewWindow : Window
{
    private static readonly Brush LightAccent = MetricDisplay.MakeBrush("#007B71");
    private static readonly Brush DarkAccent = MetricDisplay.MakeBrush("#37E2CA");
    private static readonly Brush LightForeground = MetricDisplay.MakeBrush("#1D2835");
    private static readonly Brush DarkForeground = MetricDisplay.MakeBrush("#F0F3F7");
    private static readonly Brush LightSecondary = MetricDisplay.MakeBrush("#556476");
    private static readonly Brush DarkSecondary = MetricDisplay.MakeBrush("#B2BECD");
    private static readonly Brush LightSurface = MetricDisplay.MakeBrush("#70FFFFFF");
    private static readonly Brush DarkSurface = MetricDisplay.MakeBrush("#453E5779");
    private static readonly Brush LightOutline = MetricDisplay.MakeBrush("#D6DEE9");
    private static readonly Brush DarkOutline = MetricDisplay.MakeBrush("#414C59");
    private static readonly Brush LightHover = MetricDisplay.MakeBrush("#DDE8F2");
    private static readonly Brush DarkHover = MetricDisplay.MakeBrush("#3A4959");
    private bool? lightTheme;
    private int networkRequest;
    private string? networkState, networkSourceError;
    private readonly List<(TextBlock Label, TextBlock Name, TextBlock Value)> gpuRows = [];
    private bool measureHeight = true;
    private double naturalHeight;
    private readonly double preferredWidth;
    private (TaskbarFrame Frame, Rectangle Bar)? anchor;
    private nint handle;
    /// <summary>
    /// 概览的屏幕物理像素边界，用于判断点外输入。
    /// </summary>
    internal Rectangle ScreenBounds { get; private set; }

    internal event EventHandler? SettingsRequested;
    internal event EventHandler? ProcessesRequested;

    /// <summary>
    /// 初始化概览并安排一次后台硬件规格查询。
    /// </summary>
    public OverviewWindow()
    {
        InitializeComponent();
        preferredWidth = Width;
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            TaskbarDock.MakeNonActivating(handle);
        };
        _ = LoadHardware();
    }

    /// <summary>打开概览时读取一次接口来源名称和链路状态，不在每秒指标链内重复查元数据。</summary>
    internal async Task RefreshNetworkSource(Guid? selected)
    {
        int request = ++networkRequest;
        networkSourceError = null;
        SetNetworkSource(selected.HasValue ? "读取接口…" : "全部活动实体网卡");
        UpdateNetworkStatus();
        RefreshHeight();
        if (selected is not { } id) return;
        try
        {
            var adapters = await Task.Run(PhysicalNetwork.ReadAdapters);
            if (request != networkRequest) return;
            string source = adapters.FirstOrDefault(adapter => adapter.Id == id)?.ToString() ?? "指定网卡不存在";
            SetNetworkSource(source);
        }
        catch (Win32Exception error)
        {
            if (request != networkRequest) return;
            networkSourceError = "接口来源读取失败：" + Diagnostics.Error(error);
            SetNetworkSource("接口来源不可用", networkSourceError);
            UpdateNetworkStatus();
            RefreshHeight();
        }
    }

    /// <summary>接口名称和完整说明同步更新，读取中或失败时不残留上一统计范围的提示。</summary>
    private void SetNetworkSource(string source, string? detail = null)
    {
        NetworkSource.Text = source;
        NetworkSource.ToolTip = detail ?? source;
        NetworkExplanation.Text = (detail ?? source) + "\n接口吞吐包含局域网，不等于互联网计费用量；进程流量单独统计，不与网卡强行配平。切换接口后从新统计段累计。";
    }

    private async Task LoadHardware()
    {
        var hardware = await Task.Run(HardwareSpecs.Read).ConfigureAwait(false);
        if (Dispatcher.HasShutdownStarted)
            return;
        await Dispatcher.InvokeAsync(() =>
        {
            ShowHardware(CpuSpec, hardware.Cpu);
            ShowHardware(MemorySpec, hardware.Memory);
            ShowHardware(DiskSpec, hardware.Disk);
            measureHeight = true;
            RefreshHeight();
        });
    }

    private static void ShowHardware(TextBlock text, string? value)
    {
        if (value is null)
            return;
        text.Text = value;
        text.ToolTip = value;
        text.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 按内容测量所需高度，贴近监控条显示或移动窗口。
    /// </summary>
    internal void Anchor(TaskbarFrame frame, Rectangle bar)
    {
        anchor = (frame, bar);
        var area = TaskbarDock.OverviewArea(frame, bar);
        double widthDip = Math.Min(preferredWidth, area.Width / frame.Scale);
        // 打开或补齐硬件规格时重新量高，常规贴靠只更新屏幕位置。
        if (!IsVisible || measureHeight || Width != widthDip)
        {
            Width = widthDip;
            // 先消化子控件的布局失效，避免直接 Measure 命中尚未向上传播的旧高度。
            Card.UpdateLayout();
            Card.Measure(new Size(Width, double.PositiveInfinity));
            naturalHeight = Math.Ceiling(Card.DesiredSize.Height);
            measureHeight = false;
        }
        if (!IsVisible) BodyScroll.ScrollToTop();
        // 小工作区只约束视口，指标内容保持可滚动，标题和操作始终可见。
        Height = Math.Min(naturalHeight, area.Height / frame.Scale);
        new WindowInteropHelper(this).EnsureHandle();
        int width = Math.Min(area.Width, (int)Math.Round(Width * frame.Scale));
        int height = Math.Min(area.Height, (int)Math.Ceiling(Height * frame.Scale));
        int x = Math.Clamp(bar.Right - width, area.Left, area.Right - width);
        int y = bar.Top >= area.Bottom ? area.Bottom - height : area.Top;
        var bounds = new Rectangle(x, y, width, height);
        ScreenBounds = bounds;
        TaskbarDock.Place(handle, bounds, frame.Handle);
        if (!IsVisible)
        {
            Show();
            TaskbarDock.Place(handle, bounds, frame.Handle);
        }
    }

    /// <summary>内容与外窗在同一次 UI 刷新内对齐，不等待下一次任务栏健康检查再缩放。</summary>
    private void RefreshHeight()
    {
        if (IsVisible && measureHeight && anchor is { } current)
            Anchor(current.Frame, current.Bar);
    }

    /// <summary>
    /// 根据最新快照、深浅配色和阈值刷新各项指标。
    /// </summary>
    internal void Render(SystemSample sample, Preferences settings, bool light, TrendPoint[]? history = null)
    {
        var palette = light ? MetricDisplay.Light : MetricDisplay.Dark;
        var accent = light ? LightAccent : DarkAccent;
        if (lightTheme != light)
        {
            lightTheme = light;
            Card.Background = (Brush)FindResource(light ? "OverviewLightCard" : "OverviewDarkCard");
            Card.BorderBrush = light ? LightOutline : DarkOutline;
            Foreground = light ? LightForeground : DarkForeground;
            Resources["OverviewSecondary"] = light ? LightSecondary : DarkSecondary;
            Resources["OverviewSurface"] = light ? LightSurface : DarkSurface;
            Resources["OverviewHover"] = light ? LightHover : DarkHover;
            Resources["OverviewAccent"] = accent;
            DownloadArrow.Foreground = palette.Normal;
            UploadArrow.Foreground = palette.Upload;
        }

        // 蓝雾配色只用于正常态，提示与告警仍使用统一状态色。
        Brush UsageColor(Brush status, bool memory = false) =>
            status == palette.Normal ? (memory ? accent : Foreground) : status;

        SetUsage(CpuBar, CpuPercent, sample.Cpu,
            UsageColor(palette.Status(sample.Cpu, settings.CpuWarn, settings.CpuAlert)), palette.Track);
        SetUsage(MemoryBar, MemoryPercent, sample.Memory,
            UsageColor(palette.Status(sample.Memory, settings.MemWarn, settings.MemAlert), memory: true), palette.Track);
        MemoryInfo.Text = sample.MemoryTotal > 0
            ? $"{sample.MemoryUsed / 1073741824d:0.0} / {sample.MemoryTotal / 1073741824d:0.0} GiB" : "—";

        double? diskFree = sample.DiskTotal > 0 ? 100d * sample.DiskAvailable / sample.DiskTotal : null;
        double? diskUsed = 100 - diskFree;
        SetUsage(DiskBar, DiskPercent, diskUsed,
            UsageColor(palette.Status(diskFree, settings.DiskWarn, settings.DiskAlert, reverse: true)), palette.Track);
        DiskInfo.Text = sample.DiskTotal > 0
            ? $"可用 {sample.DiskAvailable / 1073741824d:0} / {sample.DiskTotal / 1073741824d:0} GiB" : "—";
        MemoryInfo.ToolTip = MemoryInfo.Text;
        DiskInfo.ToolTip = DiskInfo.Text;

        (Download.Foreground, Upload.Foreground) = palette.Network(sample, settings);
        Download.Text = MetricDisplay.Bytes(sample.Download, rate: true);
        Upload.Text = MetricDisplay.Bytes(sample.Upload, rate: true);
        Received.Text = MetricDisplay.Bytes(sample.Received);
        Sent.Text = MetricDisplay.Bytes(sample.Sent);
        Received.ToolTip = Sent.ToolTip = $"本统计段开始于 {sample.NetworkStartedAt:G}；累计仅含已取得的有效接口整数增量。";
        networkState = sample.NetworkState;
        UpdateNetworkStatus();
        NetworkStatus.Foreground = DiskStatus.Foreground = GpuStatus.Foreground = palette.Warning;
        CpuPercent.ToolTip = sample.CpuState ?? "PDH 忙碌时间占比，非 Processor Utility";
        MemoryPercent.ToolTip = sample.MemoryState;
        string volume = System.IO.Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        DiskIoLabel.Text = "系统卷 " + volume;
        DiskRead.Text = MetricDisplay.Bytes(sample.DiskRead, rate: true);
        DiskWrite.Text = MetricDisplay.Bytes(sample.DiskWrite, rate: true);
        DiskRead.ToolTip = DiskWrite.ToolTip = sample.DiskState;
        string? diskPending = DetailPendingText(sample.DiskState);
        DiskPending.Text = diskPending;
        DiskPending.ToolTip = sample.DiskState;
        DiskPending.Visibility = diskPending is null ? Visibility.Collapsed : Visibility.Visible;
        DiskReadout.Visibility = DiskWriteout.Visibility = diskPending is null ? Visibility.Visible : Visibility.Hidden;
        ShowStatus(DiskStatus, diskPending is null ? sample.DiskState : null);
        RenderGpus(sample);
        var points = history ?? Array.Empty<TrendPoint>();
        CpuTrend.SetData(points, TrendMetric.Cpu, palette.Normal);
        MemoryTrend.SetData(points, TrendMetric.Memory, accent);
        DownloadTrend.SetData(points, TrendMetric.Download, palette.Normal);
        UploadTrend.SetData(points, TrendMetric.Upload, palette.Upload);
        RefreshHeight();
    }

    /// <summary>异常与读取状态独立于被省略的接口名称，不把说明压成不可读的长行。</summary>
    private void UpdateNetworkStatus() => ShowStatus(NetworkStatus, networkSourceError is null ? networkState :
        networkState is null ? networkSourceError : networkSourceError + "\n" + networkState);

    /// <summary>状态变化时重新量高；正常读数刷新不重复测量整个面板。</summary>
    private void ShowStatus(TextBlock text, string? state)
    {
        string next = state ?? "";
        if (text.Text == next) return;
        text.Text = next;
        text.Visibility = next.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        measureHeight = true;
    }

    /// <summary>只压缩已约定的纯等待状态；任何失败或组合状态仍须完整展示，不能因含有“预热”而隐藏。</summary>
    internal static string? DetailPendingText(string? state) => state switch
    {
        "采样中" => "等待采样…",
        "系统卷读写预热中" or "GPU 预热中" => "预热中…",
        _ => null
    };

    /// <summary>按显卡行数复用控件，只刷新读数；长名称省略并保留完整提示。</summary>
    private void RenderGpus(SystemSample sample)
    {
        string? pending = DetailPendingText(sample.GpuState);
        int count = Math.Max(1, pending is null ? sample.Gpus.Count : Math.Max(gpuRows.Count, sample.Gpus.Count));
        while (gpuRows.Count < count)
        {
            var row = new Grid { MinHeight = 24, Margin = new Thickness(0, gpuRows.Count == 0 ? 0 : 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.SetResourceReference(ForegroundProperty, "OverviewSecondary");
            var icon = PanelIcon.Create("IconMonitor", 13);
            icon.Margin = new Thickness(0, 0, 5, 0);
            label.Children.Add(icon);
            var title = new TextBlock { Text = "GPU", FontSize = 11 };
            label.Children.Add(title);
            var name = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
            var value = new TextBlock { FontSize = 11, TextAlignment = TextAlignment.Right, FontWeight = FontWeights.SemiBold };
            Grid.SetColumn(name, 1);
            Grid.SetColumn(value, 2);
            row.Children.Add(label);
            row.Children.Add(name);
            row.Children.Add(value);
            GpuDetails.Children.Add(row);
            gpuRows.Add((title, name, value));
            measureHeight = true;
        }
        if (gpuRows.Count > count)
        {
            GpuDetails.Children.RemoveRange(count, gpuRows.Count - count);
            gpuRows.RemoveRange(count, gpuRows.Count - count);
            measureHeight = true;
        }
        for (int index = 0; index < count; index++)
        {
            var gpu = index < sample.Gpus.Count ? sample.Gpus[index] : null;
            // 重开仅沿用已识别的型号和行槽，利用率必须等待本轮有效数据，不能沿用旧百分比。
            if (gpu is not null || pending is null || gpuRows[index].Name.ToolTip is null)
            {
                var caption = GpuCaption(gpu?.Name ?? pending ?? "等待有效数据");
                gpuRows[index].Label.Text = caption.Label;
                gpuRows[index].Name.Text = caption.Name;
                gpuRows[index].Name.ToolTip = gpu?.Name;
            }
            gpuRows[index].Label.ToolTip = pending is null ? null : pending + "；型号沿用已识别信息，利用率等待新数据。";
            gpuRows[index].Value.Text = MetricDisplay.Percent(pending is null ? gpu?.Usage : null);
            gpuRows[index].Value.ToolTip = "最忙引擎利用率；" + (sample.GpuState ?? "按物理 GPU 分别统计");
        }
        ShowStatus(GpuStatus, pending is null ? sample.GpuState : null);
    }

    /// <summary>仅拆分本程序生成的物理索引后缀，不改写型号本身或推测任务管理器编号。</summary>
    internal static (string Label, string Name) GpuCaption(string name)
    {
        const string marker = "（物理 GPU ";
        int start = name.LastIndexOf(marker, StringComparison.Ordinal);
        return start > 0 && name.EndsWith('）') && int.TryParse(name.AsSpan(start + marker.Length, name.Length - start - marker.Length - 1),
            NumberStyles.None, CultureInfo.InvariantCulture, out int physical) && physical >= 0
            ? ($"GPU {physical}", name[..start]) : ("GPU", name);
    }


    private void DetailSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.HeightChanged)
            measureHeight = true;
    }

    private static void SetUsage(ProgressBar bar, TextBlock text, double? value, Brush color, Brush track)
    {
        bar.Value = value ?? 0;
        bar.Foreground = text.Foreground = color;
        bar.Background = track;
        text.Text = MetricDisplay.Percent(value);
    }

    private void ShowTruncatedValue(object sender, ToolTipEventArgs e)
    {
        var text = (TextBlock)sender;
        var formatted = new FormattedText(text.Text, CultureInfo.CurrentCulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize, text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip);
        e.Handled = formatted.Width <= text.ActualWidth;
    }

    private void OpenSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void OpenProcesses(object sender, RoutedEventArgs e) => ProcessesRequested?.Invoke(this, EventArgs.Empty);
}

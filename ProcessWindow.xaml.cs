using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SysPulse;

/// <summary>
/// 展示进程快照并处理搜索、排序、固定和简化模式。
/// </summary>
public partial class ProcessWindow : Window
{
    private readonly Preferences settings;
    private readonly MonitorSession session;
    private readonly bool elevated;
    private readonly Func<(TaskbarFrame? Frame, System.Drawing.Rectangle? Bounds)> monitorAnchor;
    private readonly ObservableCollection<ProcessItem> displayed = new();
    private readonly DispatcherTimer scrollIdleTimer = new(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(250) };
    private ProcessRow[] snapshot = [];
    private ScrollViewer? scroll;
    private PopupDismissal? dismissal;
    private bool listUpdatePending;
    private bool closed;
    private bool elevating;
    private string? elevationMessage;
    private bool? lightTheme;
    private bool readOnlyMenuOpen;

    internal event EventHandler? PreferencesChanged;
    internal event EventHandler? ElevationRequested;

    /// <summary>
    /// 接入进程采样、流量会话和监控条锚点，窗口可见时才周期刷新。
    /// </summary>
    internal ProcessWindow(Preferences settings, MonitorSession session, bool elevated,
        Func<(TaskbarFrame? Frame, System.Drawing.Rectangle? Bounds)> monitorAnchor)
    {
        this.settings = settings;
        this.session = session;
        session.ProcessesChanged += ReceiveSnapshot;
        session.SystemChanged += OnSystemHeartbeat;
        session.Appearance.Changed += OnAppearanceChanged;
        this.elevated = elevated;
        this.monitorAnchor = monitorAnchor;
        InitializeComponent();
        Table.ItemsSource = displayed;
        InitializeReadOnlyMenu();
        Table.PreviewMouseWheel += (_, _) => DeferListUpdate();
        Table.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            // 同时覆盖滚轮、键盘、触控板及滚动条，不将尺寸变化误认为滚动。
            if (e.VerticalChange != 0)
                DeferListUpdate();
        }));
        scrollIdleTimer.Tick += (_, _) =>
        {
            if (Table.IsMouseCaptureWithin)
                return;
            scrollIdleTimer.Stop();
            if (listUpdatePending)
                UpdateList(preserveScroll: true);
        };
        ApplyPreferences();
        Deactivated += (_, _) => DismissIfTemporary();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                session.SetProcesses(false);
                scrollIdleTimer.Stop();
                listUpdatePending = false;
            }
            UpdateDismissal();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
        Closed += (_, _) =>
        {
            closed = true;
            session.SetProcesses(false);
            session.ProcessesChanged -= ReceiveSnapshot;
            session.SystemChanged -= OnSystemHeartbeat;
            session.Appearance.Changed -= OnAppearanceChanged;
            scrollIdleTimer.Stop();
            listUpdatePending = false;
            dismissal?.Dispose();
            dismissal = null;
        };
    }

    /// <summary>
    /// 显示并激活进程明细，管理员实例会请求开始流量采集。
    /// </summary>
    internal void Reveal()
    {
        if (!IsVisible)
        {
            snapshot = session.LatestProcesses.Rows;
            ApplyPreferences();
            UpdateList(preserveScroll: true);
            PlaceForMode(opening: true);
            Show();
        }
        Activate();
        session.SetProcesses(true);
        UpdateStatus();
    }

    /// <summary>
    /// 更新列显示、排序状态、主题和权限提示。
    /// </summary>
    internal void ApplyPreferences()
    {
        bool compact = settings.ProcessCompact;
        bool sortChanged = compact && settings.SortKey is "cpu" or "mem";
        CompactButton.IsChecked = compact;
        CompactIcon.Data = (Geometry)FindResource(compact ? "IconMaximize" : "IconMinimize");
        string compactAction = compact ? "恢复完整模式" : "切换简化模式";
        CompactButton.ToolTip = compactAction;
        System.Windows.Automation.AutomationProperties.SetName(CompactButton, compactAction);
        CpuColumn.Visibility = MemoryColumn.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        NameColumn.Width = compact ? new DataGridLength(132) : new DataGridLength(1, DataGridLengthUnitType.Star);
        if (sortChanged)
        {
            settings.SortKey = "down";
            settings.SortAscending = false;
            UpdateList(preserveScroll: true);
        }
        UpdateTheme();
        UpdateStatus();
    }

    /// <summary>
    /// 更新管理员交接提示，并在请求期间保留窗口。
    /// </summary>
    internal void SetElevationStatus(bool busy, string? message)
    {
        elevating = busy;
        elevationMessage = message;
        UpdateDismissal();
        UpdateStatus();
        if (!busy && IsVisible)
            Activate();
    }

    /// <summary>接收最新快照；滚动期间只更新快照，空闲后再合并到稳定行对象。</summary>
    private void ReceiveSnapshot(object? sender, EventArgs e)
    {
        if (closed || !IsVisible) return;
        snapshot = session.LatestProcesses.Rows;
        UpdateList(preserveScroll: true);
        UpdateStatus();
    }

    private void OnAppearanceChanged(object? sender, EventArgs e) => UpdateTheme();
    private void OnSystemHeartbeat(object? sender, EventArgs e) { if (IsVisible && !closed) UpdateStatus(); }

    /// <summary>
    /// 在滚动输入结束后合并一次最新快照，避免重排和布局阻塞下一次滚动。
    /// </summary>
    private void DeferListUpdate()
    {
        if (!IsVisible)
            return;
        scrollIdleTimer.Stop();
        scrollIdleTimer.Start();
    }

    /// <summary>
    /// 用最新快照筛选、排序，仅增删或移动必要的行，并保留滚动位置。
    /// </summary>
    private void UpdateList(bool preserveScroll)
    {
        if (preserveScroll && (scrollIdleTimer.IsEnabled || Table.IsMouseCaptureWithin || readOnlyMenuOpen))
        {
            scrollIdleTimer.Start();
            listUpdatePending = true;
            return;
        }
        listUpdatePending = false;
        if (!preserveScroll)
            scrollIdleTimer.Stop();
        scroll ??= FindScroll(Table);
        string filter = Search.Text.Trim();
        var rows = Array.FindAll(snapshot, row => row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        Array.Sort(rows, Compare);
        // 未验证实例只复用显示槽；数据身份一变就清除选择，不能让 PID 复用继承原实例的选择。
        if (Table.SelectedItem is ProcessItem selected && Array.Exists(rows,
            row => StringComparer.OrdinalIgnoreCase.Equals(row.DisplayKey, selected.Key) && row.Key != selected.InstanceKey))
            Table.SelectedItem = null;
        SynchronizeRows(displayed, rows);
        EmptyText.Visibility = displayed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = snapshot.Length == 0 ? "正在读取进程…" : "没有匹配的应用";
        // 增量合并不需要回写旧偏移，否则会覆盖 ScrollViewer 排队中的滚动命令。
        if (!preserveScroll)
            scroll?.ScrollToTop();
        foreach (var column in Table.Columns)
            column.SortDirection = column.SortMemberPath != settings.SortKey ? null :
                settings.SortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending;
    }

    /// <summary>
    /// 将快照合并到稳定显示行；未知身份的 PID 槽不参与采样、缓存或 ETW 归属。
    /// </summary>
    internal static void SynchronizeRows(ObservableCollection<ProcessItem> items, IReadOnlyList<ProcessRow> rows)
    {
        var existing = items.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        var alive = rows.Select(row => row.DisplayKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = items.Count - 1; i >= 0; i--)
            if (!alive.Contains(items[i].Key))
                items.RemoveAt(i);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (i < items.Count && StringComparer.OrdinalIgnoreCase.Equals(items[i].Key, row.DisplayKey))
            {
                items[i].Update(row);
                continue;
            }
            if (!existing.TryGetValue(row.DisplayKey, out var item))
                items.Insert(i, new ProcessItem(row));
            else
            {
                item.Update(row);
                int previous = items.IndexOf(item);
                if (previous != i)
                    items.Move(previous, i);
            }
        }
    }

    private int Compare(ProcessRow a, ProcessRow b)
    {
        int name = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        if (name == 0) name = StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key);
        if (settings.SortKey == "name")
            return settings.SortAscending ? name : -name;
        double? x = SortValue(a);
        double? y = SortValue(b);
        if (x.HasValue != y.HasValue)
            return x.HasValue ? -1 : 1;
        int comparison = Nullable.Compare(x, y);
        return comparison == 0 ? name : settings.SortAscending ? comparison : -comparison;
    }

    private double? SortValue(ProcessRow row) => settings.SortKey switch
    {
        "cpu" => row.Cpu,
        "mem" => row.Memory,
        "up" => row.Upload,
        _ => row.Download
    };

    private void UpdateStatus()
    {
        string? failure = session.TrafficFailure;
        bool retrying = session.TrafficRetryPending;
        bool hasTraffic = Array.Exists(snapshot, row => row.Download.HasValue);
        var latest = session.LatestProcesses;
        bool stale = latest.Timestamp != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(latest.Timestamp).TotalSeconds > 1.5;
        string status = elevationMessage ?? latest.State ?? (retrying ? "正在重新初始化 ETW…" :
            stale ? "显示上次缓存，正在读取当前数据…" : !elevated ? "进程流量需要管理员权限。" :
            failure ?? session.TrafficPeriodState ?? (hasTraffic ? "ETW 本周期有效；接口吞吐另计。" : "正在采集进程流量…"));
        StatusText.Text = status;
        StatusText.ToolTip = status;
        TrafficAction.Visibility = !elevated || failure is not null ? Visibility.Visible : Visibility.Collapsed;
        TrafficActionText.Text = elevated ? "重试" : "以管理员身份重启";
        System.Windows.Automation.AutomationProperties.SetName(TrafficAction, TrafficActionText.Text);
        TrafficAction.IsEnabled = !elevating && !retrying;
    }

    private void UpdateTheme()
    {
        bool light = settings.Theme == ColorTheme.Light || (settings.Theme == ColorTheme.System && session.Appearance.AppsLight);
        if (light == lightTheme)
            return;
        lightTheme = light;
#pragma warning disable WPF0001
        ThemeMode = light ? ThemeMode.Light : ThemeMode.Dark;
#pragma warning restore WPF0001
        Background = MetricDisplay.MakeBrush(light ? "#F5F7FA" : "#1E2228");
        Foreground = MetricDisplay.MakeBrush(light ? "#191D24" : "#F0F2F5");
        Resources["ProcessHeader"] = MetricDisplay.MakeBrush(light ? "#EAF0F6" : "#2D3641");
        Resources["ProcessHeaderHover"] = MetricDisplay.MakeBrush(light ? "#DDE8F4" : "#394653");
        Resources["ProcessHeaderLine"] = MetricDisplay.MakeBrush(light ? "#D3DDE8" : "#43505E");
        Resources["ProcessHeaderText"] = MetricDisplay.MakeBrush(light ? "#41536A" : "#C5D0DF");
        Resources["ProcessText"] = Foreground;
        Resources["ProcessSurface"] = MetricDisplay.MakeBrush(light ? "#FFFFFF" : "#252B33");
        Resources["ProcessAlternate"] = MetricDisplay.MakeBrush(light ? "#F2F6FA" : "#29313A");
        Resources["ProcessSelected"] = MetricDisplay.MakeBrush(light ? "#D8EAF9" : "#314D68");
    }

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer scrollViewer)
            return scrollViewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScroll(VisualTreeHelper.GetChild(root, i)) is { } result)
                return result;
        }
        return null;
    }

    private void SortColumn(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        string key = e.Column.SortMemberPath;
        settings.SortAscending = key == settings.SortKey ? !settings.SortAscending : key == "name";
        settings.SortKey = key;
        UpdateList(preserveScroll: false);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchHint();
        if (Table is not null)
            UpdateList(preserveScroll: false);
    }

    private void SearchFocusChanged(object sender, KeyboardFocusChangedEventArgs e) => UpdateSearchHint();

    private void UpdateSearchHint()
    {
        if (SearchHint is not null)
            SearchHint.Visibility = Search.Text.Length == 0 && !Search.IsKeyboardFocusWithin
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NameToolTipOpening(object sender, ToolTipEventArgs e)
    {
        var text = (TextBlock)sender;
        var formatted = new FormattedText(text.Text, CultureInfo.CurrentCulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize, text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip);
        e.Handled = formatted.Width <= text.ActualWidth;
    }

    private void TogglePin(object sender, RoutedEventArgs e)
    {
        Topmost = PinButton.IsChecked == true;
        string action = Topmost ? "取消固定窗口" : "固定窗口";
        PinButton.ToolTip = action;
        System.Windows.Automation.AutomationProperties.SetName(PinButton, action);
        UpdateDismissal();
    }

    /// <summary>仅提供用户主动请求的只读操作，路径未知时不猜测、不执行目标程序。</summary>
    private void InitializeReadOnlyMenu()
    {
        var menu = new ContextMenu();
        MenuItem Add(string title, Action<ProcessItem> action)
        {
            var command = new MenuItem { Header = title };
            command.Click += (_, _) =>
            {
                if (Table.SelectedItem is not ProcessItem item) return;
                try { action(item); }
                catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or ExternalException or InvalidOperationException)
                { StatusText.Text = "操作失败：" + Diagnostics.Error(error); StatusText.ToolTip = StatusText.Text; }
            };
            menu.Items.Add(command);
            return command;
        }
        var view = Add("查看完整路径", item => MessageBox.Show(this, item.Path!, "可执行文件路径", MessageBoxButton.OK, MessageBoxImage.Information));
        var copy = Add("复制完整路径", item => Clipboard.SetText(item.Path!));
        var pids = Add("复制 PID 列表", item => Clipboard.SetText(string.Join(", ", item.Pids)));
        var folder = Add("打开所在文件夹", item =>
        {
            var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true };
            start.ArgumentList.Add("/select,");
            start.ArgumentList.Add(item.Path!);
            using var process = Process.Start(start);
        });
        Table.ContextMenu = menu;
        Table.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(Table, source) is DataGridRow row)
                Table.SelectedItem = row.Item;
        };
        Table.ContextMenuOpening += (_, e) =>
        {
            if (Table.SelectedItem is not ProcessItem item) { e.Handled = true; return; }
            view.IsEnabled = copy.IsEnabled = folder.IsEnabled = !string.IsNullOrEmpty(item.Path);
            view.ToolTip = item.Path ?? "权限不足或路径无法可靠取得";
            pids.IsEnabled = item.Pids.Count > 0;
            readOnlyMenuOpen = true;
            UpdateDismissal();
        };
        menu.Closed += (_, _) => { readOnlyMenuOpen = false; UpdateDismissal(); };
    }

    private void UpdateDismissal()
    {
        dismissal?.Dispose();
        dismissal = null;
        if (!IsVisible || PinButton.IsChecked == true || elevating || closed || readOnlyMenuOpen)
            return;

        // 点击不获取焦点的监控条时，窗口不会收到 Deactivated，仍要按点外规则收起。
        dismissal = new PopupDismissal((x, y) =>
        {
            var topLeft = PointToScreen(new Point());
            var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
            if (x < topLeft.X || x >= bottomRight.X || y < topLeft.Y || y >= bottomRight.Y)
                DismissIfTemporary();
        }, DismissIfTemporary);
    }

    private void DismissIfTemporary()
    {
        if (!closed && IsVisible && !elevating && !readOnlyMenuOpen && PinButton.IsChecked != true)
            Hide();
    }

    private void ToggleCompact(object sender, RoutedEventArgs e)
    {
        // 应用已有配置也会改变勾选状态，无需再次应用或触发保存。
        if (settings.ProcessCompact == (CompactButton.IsChecked == true))
            return;
        settings.ProcessCompact = CompactButton.IsChecked == true;
        ApplyPreferences();
        PlaceForMode(opening: false);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 按当前模式计算尺寸，使用一次窗口移动操作完成贴靠和缩放。
    /// </summary>
    private void PlaceForMode(bool opening)
    {
        bool compact = settings.ProcessCompact;
        int width = compact ? 356 : 540;
        int height = compact ? 340 : 470;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var (taskbar, bar) = monitorAnchor();
        // 位置与大小一次提交，避免 WPF 先从左上角缩放、再由 Win32 把窗体挪回任务栏。
        var position = taskbar is { } frame && bar is { } bounds
            ? TaskbarDock.NextToBar(frame, bounds, (int)Math.Ceiling(width * frame.Scale), (int)Math.Ceiling(height * frame.Scale))
            : TaskbarDock.ResizeInWorkArea(handle, width, height, opening && !IsLoaded);
        TaskbarDock.Move(handle, position);
    }

    private void TrafficActionClick(object sender, RoutedEventArgs e)
    {
        if (elevated) { session.RetryTraffic(); UpdateStatus(); }
        else ElevationRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Dismiss(object sender, RoutedEventArgs e) => Hide();
}

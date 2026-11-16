using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Media.Imaging;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SysPulse;

/// <summary>
/// 提供分区设置页面，即时更新共享配置并通知调用方应用和保存。
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly Preferences settings;
    private readonly Action changed;
    private readonly Appearance appearance;
    private bool closed;
    private readonly TextBlock saveStatus = new() { VerticalAlignment = VerticalAlignment.Center };

    /// <summary>
    /// 从当前配置创建设置窗口，每次编辑后调用变更回调。
    /// </summary>
    public SettingsWindow(Preferences settings, Appearance appearance, Action changed, Action retrySampling)
    {
        this.settings = settings;
        this.changed = changed;
        this.appearance = appearance;
        Title = "SysPulse 设置";
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/SysPulse.ico"));
        Width = 760;
        Height = 560;
        MinWidth = 500;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 42,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        });
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        ApplyTheme();
        appearance.Changed += OnAppearanceChanged;
        Closed += (_, _) => { closed = true; appearance.Changed -= OnAppearanceChanged; };
        ShowSaveResult(!settings.SaveBlocked);

        var layout = new DockPanel();
        Content = layout;
        var titleBar = new DockPanel { Height = 42, LastChildFill = true, Margin = new Thickness(18, 0, 10, 0) };
        DockPanel.SetDock(titleBar, Dock.Top);
        layout.Children.Add(titleBar);
        var dismiss = new Button
        {
            Content = PanelIcon.Create("IconClose", 15), Width = 32, Height = 27,
            Padding = new Thickness(0), ToolTip = "关闭"
        };
        AutomationProperties.SetName(dismiss, "关闭");
        WindowChrome.SetIsHitTestVisibleInChrome(dismiss, true);
        dismiss.Click += (_, _) => Close();
        DockPanel.SetDock(dismiss, Dock.Right);
        titleBar.Children.Add(dismiss);
        titleBar.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Image { Source = Icon, Width = 20, Height = 20, Margin = new Thickness(0, 0, 7, 0) },
                new TextBlock { Text = Title, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }
            }
        });

        var footer = new DockPanel { Margin = new Thickness(18, 10, 18, 12) };
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        var close = new Button { Content = "关闭", MinWidth = 72 };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        footer.Children.Add(saveStatus);

        var pages = new DockPanel();
        layout.Children.Add(pages);
        var navigation = new ListBox
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemContainerStyle = (Style)FindResource("SettingsNavigationItem")
        };
        var navigationCard = new Border
        {
            Child = navigation, Width = 172, Padding = new Thickness(6),
            CornerRadius = new CornerRadius(10), Margin = new Thickness(12, 8, 0, 0)
        };
        navigationCard.SetResourceReference(Border.BackgroundProperty, "SettingsNavSurface");
        DockPanel.SetDock(navigationCard, Dock.Left);
        pages.Children.Add(navigationCard);
        var body = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        pages.Children.Add(body);

        var content = new[]
        {
            Page("指标标签",
                Card(Row("CPU 标签", LabelInput(settings.CpuLabel, value => settings.CpuLabel = value)),
                    Row("内存标签", LabelInput(settings.MemLabel, value => settings.MemLabel = value)))),
            Page("外观与刷新",
                Card(MetricOrderList()),
                Card(Row("监控条样式", Choice(["环形 + 双行", "双行分组", "CPU / 内存单行"], (int)settings.BarStyle, i => settings.BarStyle = (BarStyle)i)),
                    Row("主题", Choice(["跟随系统", "深色", "浅色"], (int)settings.Theme, i => { settings.Theme = (ColorTheme)i; ApplyTheme(); })),
                    SliderRow("字号", 11, 16, settings.FontSize, "", value => settings.FontSize = value),
                    SliderRow("背景不透明度", 0, 100, settings.BarOpacity, "%", value => settings.BarOpacity = value),
                    SliderRow("刷新间隔", 500, 10000, settings.RefreshMs, " ms", value => settings.RefreshMs = value, step: 100)),
                Card(new TextBlock { Text = "监控条图标", FontWeight = FontWeights.SemiBold },
                    Row("CPU 图标", Choice(["无图标", "芯片", "脉冲"], (int)settings.CpuIcon, i => settings.CpuIcon = (CpuIconStyle)i)),
                    Row("内存图标", Choice(["无图标", "内存条", "层叠"], (int)settings.MemoryIcon, i => settings.MemoryIcon = (MemoryIconStyle)i)),
                    Row("上下行符号", Choice(["箭头 ↓ ↑", "尖括号 ﹀ ︿", "三角形 ▽ △"], (int)settings.NetworkSymbols,
                        i => settings.NetworkSymbols = (NetworkSymbolStyle)i)))),
            Page("状态色与阈值",
                Thresholds("CPU 使用率", 1, 99, 2, 100, settings.CpuWarn, settings.CpuAlert, "%", (w, a) => { settings.CpuWarn = w; settings.CpuAlert = a; }),
                Thresholds("内存使用率", 1, 99, 2, 100, settings.MemWarn, settings.MemAlert, "%", (w, a) => { settings.MemWarn = w; settings.MemAlert = a; }),
                Thresholds("磁盘剩余", 2, 98, 1, 97, settings.DiskWarn, settings.DiskAlert, "%", (w, a) => { settings.DiskWarn = w; settings.DiskAlert = a; }, reverse: true),
                Thresholds("上下行合计", 1, 200, 2, 500, settings.NetWarn, settings.NetAlert, " MiB/s", (w, a) => { settings.NetWarn = w; settings.NetAlert = a; })),
            NetworkPage(retrySampling),
            StartupPage()
        };
        (string Label, string Icon)[] sections =
            [("指标标签", "IconMonitor"), ("外观与刷新", "IconPalette"), ("状态色与阈值", "IconSettings"), ("网络与诊断", "IconActivity"), ("开机、权限与版本", "IconInfo")];
        foreach (var (label, icon) in sections)
        {
            var symbol = PanelIcon.Create(icon, 15);
            symbol.Margin = new Thickness(0, 0, 8, 0);
            var item = new ListBoxItem
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { symbol, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } }
                }
            };
            AutomationProperties.SetName(item, label);
            navigation.Items.Add(item);
        }
        navigation.SelectionChanged += (_, _) =>
        {
            if (navigation.SelectedIndex >= 0)
            {
                body.Content = content[navigation.SelectedIndex];
                body.ScrollToTop();
            }
        };
        navigation.SelectedIndex = 0;
    }

    /// <summary>
    /// 自启状态以注册表为准；启动提权单独持久化，避免勾选时立即弹出 UAC。
    /// </summary>
    private StackPanel StartupPage()
    {
        var autoStart = new CheckBox { Content = "登录 Windows 后启动 SysPulse", Margin = new Thickness(0, 3, 0, 3) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "SettingsSecondary");
        bool refreshing = false;
        void RefreshAutoStart()
        {
            refreshing = true;
            autoStart.IsEnabled = StartupIntegration.TryRead(out bool enabled, out string? error);
            autoStart.IsChecked = enabled;
            status.Text = error ?? "自启使用当前程序的绝对路径；移动程序后请重新启用。";
            refreshing = false;
        }
        void SetAutoStart(object sender, RoutedEventArgs e)
        {
            if (refreshing)
                return;
            if (!StartupIntegration.TrySet(autoStart.IsChecked == true, out string? error))
            {
                RefreshAutoStart();
                status.Text = error;
            }
        }
        autoStart.Checked += SetAutoStart;
        autoStart.Unchecked += SetAutoStart;
        Activated += (_, _) => RefreshAutoStart();
        RefreshAutoStart();
        return Page("开机、权限与版本",
            Card(autoStart, status),
            Card(Check("启动时申请管理员权限（下次启动生效）", settings.ElevateOnStart,
                    value => settings.ElevateOnStart = value),
                new TextBlock
                {
                    Text = "默认关闭。开启后每次普通权限启动会请求 UAC，登录自启时也可能弹出。取消后保留普通实例；仅查看整机指标无需管理员权限。",
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
                }),
            Card(new TextBlock { Text = $"SysPulse {App.VersionLabel}" },
                new TextBlock { Text = "更改即时应用。唯一配置保存在 EXE 同目录。", TextWrapping = TextWrapping.Wrap }));
    }

    /// <summary>
    /// 在窗口底部反馈配置写入结果。
    /// </summary>
    internal void ShowSaveResult(bool success)
    {
        saveStatus.Text = success ? "" : settings.LoadError ?? settings.SaveError ?? "无法写入唯一配置。";
        saveStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        saveStatus.ToolTip = saveStatus.Text;
    }

    private void ApplyTheme()
    {
        // Fluent 自带深浅控件模板；主题调用集中在此，便于随 WPF 版本升级。
#pragma warning disable WPF0001
        ThemeMode = settings.Theme switch
        {
            ColorTheme.Light => ThemeMode.Light,
            ColorTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System
        };
#pragma warning restore WPF0001
        bool light = settings.Theme == ColorTheme.Light || (settings.Theme == ColorTheme.System && appearance.AppsLight);
        Background = MetricDisplay.MakeBrush(light ? "#F5F7FA" : "#1E2228");
        Foreground = MetricDisplay.MakeBrush(light ? "#191D24" : "#F0F2F5");
        Resources["SettingsCard"] = MetricDisplay.MakeBrush(light ? "#FBFCFD" : "#252B33");
        Resources["SettingsOutline"] = MetricDisplay.MakeBrush(light ? "#DDE5EC" : "#3D4652");
        Resources["SettingsNavSurface"] = MetricDisplay.MakeBrush(light ? "#EEF2F6" : "#252B33");
        Resources["SettingsNavHover"] = MetricDisplay.MakeBrush(light ? "#E3EBF3" : "#2F3946");
        Resources["SettingsNavSelected"] = MetricDisplay.MakeBrush(light ? "#DCEBFA" : "#31465B");
        Resources["SettingsSecondary"] = MetricDisplay.MakeBrush(light ? "#526578" : "#B2C0CF");
        Resources["SettingsWarning"] = light ? MetricDisplay.Light.Warning : MetricDisplay.Dark.Warning;
        Resources["SettingsAccent"] = MetricDisplay.MakeBrush(light ? "#255A83" : "#ACD9F7");
    }

    private void OnAppearanceChanged(object? sender, EventArgs e) => ApplyTheme();

    /// <summary>接口元数据仅在打开设置或主动刷新时读取，选择 GUID 后开始新的累计段。</summary>
    private StackPanel NetworkPage(Action retrySampling)
    {
        static TextBlock Hint(string text)
        {
            var hint = new TextBlock { Text = text, FontSize = 12, LineHeight = 18, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "SettingsSecondary");
            return hint;
        }
        var choice = new ComboBox { MinHeight = 32, IsEnabled = false };
        AutomationProperties.SetName(choice, "统计网卡");
        var itemText = new FrameworkElementFactory(typeof(TextBlock));
        itemText.SetBinding(TextBlock.TextProperty, new Binding());
        itemText.SetBinding(ToolTipProperty, new Binding());
        itemText.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        choice.ItemTemplate = new DataTemplate { VisualTree = itemText };
        var info = Hint("读取接口…");
        var refresh = new Button { Content = "刷新接口", MinHeight = 32, MinWidth = 80, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        var selector = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        selector.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        selector.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(refresh, 1);
        selector.Children.Add(choice);
        selector.Children.Add(refresh);
        bool loading = false;
        void ShowLink()
        {
            info.SetResourceReference(TextBlock.ForegroundProperty,
                choice.SelectedItem is NetworkAdapter { Connected: false } ? "SettingsWarning" : "SettingsSecondary");
            info.Text = choice.SelectedItem is NetworkAdapter adapter
                ? $"{(adapter.Connected ? "已连接" : "断开或不存在")}；接收 / 发送链路：{adapter.ReceiveSpeed / 1e9:0.##} / {adapter.TransmitSpeed / 1e9:0.##} Gbit/s"
                : "全部活动实体网卡；不重复叠加虚拟与过滤接口。";
        }
        async Task LoadAdapters()
        {
            if (loading) return;
            loading = true;
            refresh.IsEnabled = choice.IsEnabled = false;
            info.Text = "读取接口…";
            info.SetResourceReference(TextBlock.ForegroundProperty, "SettingsSecondary");
            try
            {
                var adapters = await Task.Run(PhysicalNetwork.ReadAdapters);
                if (closed) return;
                choice.Items.Clear();
                choice.Items.Add("全部活动实体网卡（默认）");
                foreach (var adapter in adapters.OrderBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase)) choice.Items.Add(adapter);
                var selected = adapters.FirstOrDefault(adapter => adapter.Id == settings.NetworkInterface);
                if (settings.NetworkInterface is { } id && selected is null)
                {
                    selected = new NetworkAdapter(id, "已选接口（当前不存在）", false, 0, 0);
                    choice.Items.Add(selected);
                }
                choice.SelectedItem = selected is null ? choice.Items[0] : selected;
                ShowLink();
                choice.IsEnabled = true;
            }
            catch (Win32Exception error)
            {
                if (closed) return;
                info.Text = "无法读取接口：" + Diagnostics.Error(error);
                info.SetResourceReference(TextBlock.ForegroundProperty, "SettingsWarning");
            }
            finally { loading = false; if (!closed) refresh.IsEnabled = true; }
        }
        choice.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            Guid? selected = (choice.SelectedItem as NetworkAdapter)?.Id;
            if (settings.NetworkInterface == selected) return;
            settings.NetworkInterface = selected;
            ShowLink();
            info.Text += "\n统计范围已改变，本次累计从新段开始。";
            changed();
        };
        refresh.Click += async (_, _) => await LoadAdapters();
        _ = LoadAdapters();

        var copy = new Button { Content = "复制诊断信息", MinHeight = 32, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        var diagnosticHeader = new DockPanel { MinHeight = 32 };
        DockPanel.SetDock(copy, Dock.Right);
        diagnosticHeader.Children.Add(copy);
        diagnosticHeader.Children.Add(new TextBlock { Text = "诊断", FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(Diagnostics.Export()); saveStatus.Text = "诊断已复制（不含进程列表、完整路径或 IP）。"; }
            catch (ExternalException) { saveStatus.Text = "剪贴板正忙，请重试。"; }
        };
        var folder = new Button { Content = "打开配置位置", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        folder.Click += (_, _) =>
        {
            try
            {
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                start.ArgumentList.Add(Path.GetDirectoryName(settings.FilePath)!);
                using var process = Process.Start(start);
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { saveStatus.Text = Diagnostics.Error(error); }
        };
        var rebuild = new Button { Content = "备份并重建配置", IsEnabled = settings.SaveBlocked, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        rebuild.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "先备份原文件，再用当前界面中的设置重建唯一配置。是否继续？", "重建配置", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            bool saved = settings.Rebuild();
            ShowSaveResult(saved);
            rebuild.IsEnabled = settings.SaveBlocked;
        };
        var networkCard = Card(new TextBlock { Text = "网络统计", FontSize = 13, FontWeight = FontWeights.SemiBold },
            selector, info, Hint("包含局域网流量，不等于互联网计费用量。\n进程流量单独统计，不与所选网卡强行配平。"));
        var retry = new Button { Content = "重新初始化采样和任务栏", MinHeight = 32, Padding = new Thickness(10, 4, 10, 4) };
        retry.Click += (_, _) => { retrySampling(); saveStatus.Text = "已请求重新初始化采样和任务栏。"; };
        var diagnosticCard = Card(diagnosticHeader, retry, Hint("仅复制内存诊断，不记录逐秒日志，不上传数据。"));
        networkCard.Padding = diagnosticCard.Padding = new Thickness(10);
        return Page("网络与诊断", networkCard, diagnosticCard,
            Card(new TextBlock { Text = "唯一配置：" + settings.FilePath, TextWrapping = TextWrapping.Wrap }, folder, rebuild));
    }

    private static StackPanel Page(string title, params UIElement[] elements)
    {
        var panel = new StackPanel { Margin = new Thickness(20, 8, 18, 0) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        foreach (var item in elements)
            panel.Children.Add(item);
        return panel;
    }

    private static Border Card(params UIElement[] elements)
    {
        var panel = new StackPanel();
        foreach (var item in elements)
            panel.Children.Add(item);
        var card = new Border
        {
            Child = panel, CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(1)
        };
        card.SetResourceReference(Border.BackgroundProperty, "SettingsCard");
        card.SetResourceReference(Border.BorderBrushProperty, "SettingsOutline");
        return card;
    }

    private static DockPanel Row(string label, UIElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), MinHeight = 30 };
        var title = new TextBlock { Text = label, Width = 125, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(title);
        row.Children.Add(control);
        return row;
    }

    private CheckBox Check(string label, bool initial, Action<bool> update)
    {
        var check = new CheckBox { Content = label, IsChecked = initial, Margin = new Thickness(0, 3, 0, 3) };
        void Update(object sender, RoutedEventArgs e) { update(check.IsChecked == true); changed(); }
        check.Checked += Update;
        check.Unchecked += Update;
        return check;
    }

    private TextBox LabelInput(string initial, Action<string> update)
    {
        var box = new TextBox { Text = initial, MaxLength = 6, Width = 155, HorizontalAlignment = HorizontalAlignment.Left };
        box.TextChanged += (_, _) => { update(box.Text); changed(); };
        return box;
    }

    /// <summary>
    /// 管理指标启用状态，并提供仅包含启用项的排序列表。
    /// </summary>
    private StackPanel MetricOrderList()
    {
        var list = new ListBox
        {
            AllowDrop = true, Margin = new Thickness(0, 5, 0, 6),
            Template = (ControlTemplate)FindResource("SettingsMetricOrderList")
        };
        AutomationProperties.SetName(list, "指标顺序");
        ListBoxItem CreateItem(BarMetric metric)
        {
            string name = metric switch { BarMetric.Network => "流量", BarMetric.Cpu => "CPU", _ => "内存" };
            var icon = PanelIcon.Create("IconList", 13);
            icon.Margin = new Thickness(0, 0, 8, 0);
            var item = new ListBoxItem
            {
                Tag = metric,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { icon, new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center } }
                }
            };
            AutomationProperties.SetName(item, name);
            return item;
        }

        var up = new Button { Content = "上移", Margin = new Thickness(0, 0, 6, 0) };
        var down = new Button { Content = "下移" };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { up, down }
        };
        var hint = new TextBlock { Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "SettingsSecondary");

        void RefreshItems()
        {
            object? selected = (list.SelectedItem as ListBoxItem)?.Tag;
            list.Items.Clear();
            var (first, second, third) = settings.GetMetricOrder();
            foreach (var metric in new[] { first, second, third }.Where(settings.IsMetricEnabled))
            {
                var item = CreateItem(metric);
                list.Items.Add(item);
                if (Equals(metric, selected))
                    list.SelectedItem = item;
            }
            bool hasItems = list.Items.Count > 0;
            if (hasItems && list.SelectedIndex < 0)
                list.SelectedIndex = 0;
            list.Visibility = actions.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            hint.Text = list.Items.Count switch
            {
                0 => "当前未启用任何指标，监控条会暂时隐藏。",
                1 => "仅启用一组指标，启用其他指标后可调整顺序。",
                _ => "仅已启用的指标参与排序。可拖动，或选中后上移、下移。"
            };
        }

        void Move(int from, int to)
        {
            if (from < 0 || to < 0 || to >= list.Items.Count || from == to)
                return;
            var item = list.Items[from];
            list.Items.RemoveAt(from);
            list.Items.Insert(to, item);
            list.SelectedIndex = to;
            settings.SetEnabledMetricOrder([.. list.Items.Cast<ListBoxItem>().Select(row => (BarMetric)row.Tag)]);
            changed();
        }

        list.SelectionChanged += (_, _) =>
        {
            up.IsEnabled = list.SelectedIndex > 0;
            down.IsEnabled = list.SelectedIndex >= 0 && list.SelectedIndex < list.Items.Count - 1;
        };
        up.Click += (_, _) => Move(list.SelectedIndex, list.SelectedIndex - 1);
        down.Click += (_, _) => Move(list.SelectedIndex, list.SelectedIndex + 1);
        RefreshItems();

        ListBoxItem? ItemAt(object source) => source is DependencyObject element
            ? ItemsControl.ContainerFromElement(list, element) as ListBoxItem : null;
        Point press = default;
        ListBoxItem? pressed = null;
        ListBoxItem? dragging = null;
        list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressed = ItemAt(e.OriginalSource);
            press = e.GetPosition(list);
        };
        list.PreviewMouseLeftButtonUp += (_, _) => pressed = null;
        list.MouseLeave += (_, _) => pressed = null;
        list.PreviewMouseMove += (_, e) =>
        {
            if (pressed is null || e.LeftButton != MouseButtonState.Pressed)
                return;
            Vector delta = e.GetPosition(list) - press;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            dragging = pressed;
            pressed = null;
            try { DragDrop.DoDragDrop(list, "SysPulse.MetricOrder", DragDropEffects.Move); }
            finally { dragging = null; }
        };
        list.DragOver += (_, e) =>
        {
            e.Effects = dragging is not null && ItemAt(e.OriginalSource) is not null
                ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        list.Drop += (_, e) =>
        {
            if (dragging is not null && ItemAt(e.OriginalSource) is { } target)
                Move(list.Items.IndexOf(dragging), list.Items.IndexOf(target));
            e.Handled = true;
        };

        CheckBox Enable(string label, bool initial, Action<bool> update)
        {
            var check = Check(label, initial, value => { update(value); RefreshItems(); });
            check.Margin = new Thickness(0, 3, 16, 3);
            return check;
        }
        return new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "指标显示与顺序", FontWeight = FontWeights.SemiBold },
                new WrapPanel
                {
                    Margin = new Thickness(0, 4, 0, 3),
                    Children =
                    {
                        Enable("下载", settings.ShowDownload, value => settings.ShowDownload = value),
                        Enable("上传", settings.ShowUpload, value => settings.ShowUpload = value),
                        Enable("CPU", settings.ShowCpu, value => settings.ShowCpu = value),
                        Enable("内存", settings.ShowMemory, value => settings.ShowMemory = value)
                    }
                },
                hint,
                list,
                actions
            }
        };
    }

    private ComboBox Choice(string[] options, int initial, Action<int> update)
    {
        var combo = new ComboBox { ItemsSource = options, SelectedIndex = initial, Width = 155, HorizontalAlignment = HorizontalAlignment.Left };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0) { update(combo.SelectedIndex); changed(); }
        };
        return combo;
    }

    private DockPanel SliderRow(string label, int min, int max, int value, string unit, Action<int> update, int step = 1)
    {
        var slider = NewSlider(min, max, value, step);
        slider.ValueChanged += (_, _) => { update((int)slider.Value); changed(); };
        return Row(label, WithValue(slider, unit));
    }

    /// <summary>
    /// 创建一组联动阈值控件，拖动时维持提示与告警的先后关系。
    /// </summary>
    private Border Thresholds(string title, int warnMin, int warnMax, int alertMin, int alertMax,
        int warnValue, int alertValue, string unit, Action<int, int> update, bool reverse = false)
    {
        var warn = NewSlider(warnMin, warnMax, warnValue, 1);
        var alert = NewSlider(alertMin, alertMax, alertValue, 1);
        bool adjusting = false;
        void Changed(bool moveAlert)
        {
            if (adjusting)
                return;
            adjusting = true;
            // 两条线交叉时，由正在拖动的滑块决定，另一条线移动一个步进让位。
            if (reverse ? warn.Value <= alert.Value : warn.Value >= alert.Value)
            {
                int step = reverse ? -1 : 1;
                if (moveAlert) alert.Value = warn.Value + step;
                else warn.Value = alert.Value - step;
            }
            adjusting = false;
            update((int)warn.Value, (int)alert.Value);
            changed();
        }
        warn.ValueChanged += (_, _) => Changed(true);
        alert.ValueChanged += (_, _) => Changed(false);
        string sign = reverse ? "<" : "≥";
        return Card(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold },
            Row($"提示 {sign}", WithValue(warn, unit)), Row($"告警 {sign}", WithValue(alert, unit)));
    }

    private static Slider NewSlider(int min, int max, int value, int step) => new()
    {
        Minimum = min, Maximum = max, Value = value, TickFrequency = step, SmallChange = step,
        LargeChange = Math.Max(step, (max - min) / 10), IsSnapToTickEnabled = true, IsMoveToPointEnabled = true
    };

    private static DockPanel WithValue(Slider slider, string unit)
    {
        var row = new DockPanel();
        var value = new TextBlock { Width = 72, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        value.Text = $"{slider.Value:0}{unit}";
        slider.ValueChanged += (_, _) => value.Text = $"{slider.Value:0}{unit}";
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(value);
        row.Children.Add(slider);
        return row;
    }
}

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace SysPulse;

/// <summary>
/// 管理应用生命周期、托盘、单实例和管理员进程交接。
/// </summary>
public partial class App : Application
{
    private Mutex? instanceLock;
    private MonitorBar? bar;
    private Forms.NotifyIcon? tray;
    private Icon? appIcon;
    private Forms.ContextMenuStrip? trayMenu;
    private Preferences? preferences;
    private SettingsWindow? settingsWindow;
    private ProcessWindow? processWindow;
    private MonitorSession? session;
    private bool saveFailureNotified;
    private bool elevated;
    private bool elevationPending;
    private bool ownsInstance;
    private bool exiting;
    private string instanceName = "";
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    /// <summary>
    /// 显示产品版本；SDK 提供源码修订号时附带短提交号。
    /// </summary>
    internal static string VersionLabel
    {
        get
        {
            var assembly = typeof(App).Assembly;
            string version = assembly.GetName().Version!.ToString(3);
            string? info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            int separator = info?.IndexOf('+') ?? -1;
            string revision = separator >= 0 ? info![(separator + 1)..] : "";
            return revision.Length >= 7 ? $"{version} ({revision[..7]})" : version;
        }
    }

    /// <summary>
    /// 确认当前用户的实例身份后初始化采样器、托盘和监控条。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args is ["--selftest"])
        {
            Shutdown(SelfTest.Run());
            return;
        }

        base.OnStartup(e);

        using var identity = WindowsIdentity.GetCurrent();
        elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        instanceName = $@"Local\SysPulse-{identity.User!.Value}";
        bool handoff = e.Args is ["--handoff", _] or ["--handoff", _, "--show-processes"];
        if (!AcquireInstance(handoff, handoff ? e.Args[1] : null))
        {
            Shutdown();
            return;
        }

        preferences = Preferences.Load();
        session = new MonitorSession(Dispatcher, preferences, elevated);
        saveTimer.Tick += (_, _) => SavePreferences();
        bar = new MonitorBar(preferences, session);
        bar.ExitRequested += async (_, _) => await ExitAsync();
        bar.SettingsRequested += (_, _) => ShowSettings();
        bar.ProcessesRequested += (_, _) => ShowProcesses();
        bar.PreferencesChanged += (_, _) => ScheduleSave();
        MainWindow = bar;

        trayMenu = new Forms.ContextMenuStrip();
        var toggle = new Forms.ToolStripMenuItem();
        toggle.Click += (_, _) => bar.ToggleRequestedVisibility();
        trayMenu.Items.Add(toggle);
        trayMenu.Items.Add("进程明细", null, (_, _) => ShowProcesses());
        if (!elevated)
            trayMenu.Items.Add("以管理员身份重启", null, async (_, _) => await RestartElevated());
        trayMenu.Items.Add("设置", null, (_, _) => ShowSettings());
        var autoStart = new Forms.ToolStripMenuItem("开机自启");
        autoStart.Click += (_, _) =>
        {
            if (!StartupIntegration.TryRead(out bool enabled, out string? error) ||
                !StartupIntegration.TrySet(!enabled, out error))
                tray?.ShowBalloonTip(5000, "SysPulse", error ?? "无法更改开机自启。", Forms.ToolTipIcon.Warning);
        };
        trayMenu.Items.Add(autoStart);
        trayMenu.Items.Add("退出 SysPulse", null, async (_, _) => await ExitAsync());
        trayMenu.Opening += (_, _) =>
        {
            toggle.Text = bar.RequestedVisible ? "隐藏监控条" : "显示监控条";
            autoStart.Enabled = StartupIntegration.TryRead(out bool enabled, out string? error);
            autoStart.Checked = enabled;
            autoStart.ToolTipText = error;
            bar.SetTrayMenuOpen(true);
        };
        trayMenu.Closed += (_, _) => bar.SetTrayMenuOpen(false);

        appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        tray = new Forms.NotifyIcon
        {
            Icon = appIcon ?? SystemIcons.Application,
            Text = $"SysPulse {VersionLabel}",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                ShowSettings();
        };
        bar.Start();
        if (preferences.LoadError is { } configError)
        {
            tray.ShowBalloonTip(5000, "SysPulse 配置", configError, Forms.ToolTipIcon.Warning);
            Dispatcher.BeginInvoke(ShowSettings);
        }
        if (handoff && e.Args.Length == 3)
            Dispatcher.BeginInvoke(ShowProcesses);
        if (!handoff && preferences.ElevateOnStart && !elevated)
            Dispatcher.BeginInvoke(new Action(async () => await RestartElevated(showProcesses: false)));
    }

    /// <summary>
    /// 保存尚未写入的配置，释放托盘、采集会话和单实例锁。
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        if (saveTimer.IsEnabled)
            SavePreferences();
        tray?.Dispose();
        appIcon?.Dispose();
        trayMenu?.Dispose();
        // 系统注销等直接关闭路径也要向工作者发出停止信号。
        if (!exiting) _ = session?.StopAsync();
        if (ownsInstance)
            instanceLock?.ReleaseMutex();
        instanceLock?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Windows 注销时先保存设置，再给原采样线程一个有限的清理窗口。</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (e.Cancel || session is null) return;
        if (saveTimer.IsEnabled) SavePreferences();
        try { session.StopAsync().Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException error) { Diagnostics.Record("注销清理", Diagnostics.Error(error.GetBaseException())); }
    }

    /// <summary>正常退出和提权交接共用清理过程，等待期间保留 Dispatcher 与单实例锁。</summary>
    private async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        var cleanup = session!.StopAsync();
        if (await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(5))) != cleanup)
        {
            // 普通实例没有 ETW 会话，允许结束进程，避免慢速本机调用阻塞提权交接。
            if (!elevated) { Shutdown(); return; }
            Diagnostics.Record("退出", "仍在等待系统采集调用结束");
            tray?.ShowBalloonTip(5000, "SysPulse", "正在等待 Windows 采集会话关闭…", Forms.ToolTipIcon.Info);
        }
        // 继续泵送 UI 消息，且保留锁直到旧 ETW 会话已释放。
        try { await cleanup; }
        catch (Exception error) { Diagnostics.Record("退出故障", Diagnostics.Error(error)); }
        Shutdown();
    }

    private void ShowSettings()
    {
        if (exiting) return;
        bar!.CloseOverview();
        if (settingsWindow is null)
        {
            settingsWindow = new SettingsWindow(preferences!, session!.Appearance, () =>
            {
                bar!.ApplyPreferences();
                processWindow?.ApplyPreferences();
                ScheduleSave();
            }, () => { session!.RetrySampling(); bar!.RetryTaskbar(); });
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
        }
        if (settingsWindow.WindowState == WindowState.Minimized)
            settingsWindow.WindowState = WindowState.Normal;
        settingsWindow.Activate();
    }

    private void ShowProcesses()
    {
        if (exiting) return;
        bar!.CloseOverview();
        if (processWindow is null)
        {
            processWindow = new ProcessWindow(preferences!, session!, elevated, () => bar.ProcessAnchor);
            processWindow.PreferencesChanged += (_, _) => ScheduleSave();
            processWindow.ElevationRequested += async (_, _) => await RestartElevated();
            processWindow.Closed += (_, _) => processWindow = null;
        }
        processWindow.Reveal();
    }

    /// <summary>
    /// 按用户和桌面会话取得实例锁。管理员接管时先通知旧实例，再等待锁释放。
    /// </summary>
    private bool AcquireInstance(bool handoff, string? token)
    {
        if (!handoff)
        {
            try
            {
                instanceLock = new Mutex(true, instanceName, out ownsInstance);
                return ownsInstance;
            }
            catch (UnauthorizedAccessException)
            {
                // 普通权限启动时，已存在的管理员实例可能限制互斥锁访问。
                return false;
            }
        }

        if (!elevated || !Guid.TryParseExact(token, "N", out _))
            return false;

        try
        {
            // 只接受同一用户创建的一次性交接事件。旧实例等到新进程到达这里才退出。
            using var ready = EventWaitHandle.OpenExisting($"{instanceName}-handoff-{token}");
            instanceLock = new Mutex(true, instanceName, out ownsInstance);
            ready.Set();
            if (!ownsInstance)
            {
                try { ownsInstance = instanceLock.WaitOne(TimeSpan.FromSeconds(15)); }
                catch (AbandonedMutexException) { ownsInstance = true; }
            }
            return ownsInstance;
        }
        catch (Exception error) when (error is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 按需启动管理员实例，并在收到接管信号后退出当前实例。
    /// </summary>
    private async Task RestartElevated(bool showProcesses = true)
    {
        if (elevated || elevationPending || exiting)
            return;

        if (showProcesses)
            ShowProcesses();
        elevationPending = true;
        processWindow?.SetElevationStatus(true, "等待 Windows 管理员权限确认…");
        SavePreferences();
        string token = Guid.NewGuid().ToString("N");
        string? message = await Task.Run(() =>
        {
            try
            {
                using var ready = new EventWaitHandle(false, EventResetMode.ManualReset,
                    $"{instanceName}-handoff-{token}");
                var start = showProcesses
                    ? StartupIntegration.CreateStartInfo("--handoff", token, "--show-processes")
                    : StartupIntegration.CreateStartInfo("--handoff", token);
                start.Verb = "runas";
                using var replacement = Process.Start(start);
                return replacement is not null && ready.WaitOne(TimeSpan.FromSeconds(12))
                    ? null : "管理员实例未接管。当前实例仍在运行。";
            }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223)
            {
                return "已取消管理员权限请求。";
            }
            catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or InvalidOperationException)
            {
                return $"无法以管理员身份重启。{error.Message}";
            }
        });
        elevationPending = false;
        if (message is null)
            await ExitAsync();
        else if (showProcesses)
            processWindow?.SetElevationStatus(false, message);
        else
            tray?.ShowBalloonTip(5000, "SysPulse", message, Forms.ToolTipIcon.Warning);
    }

    /// <summary>
    /// 合并短时间内连续发生的配置更改。
    /// </summary>
    private void ScheduleSave()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }

    private void SavePreferences()
    {
        saveTimer.Stop();
        bool saved = preferences!.Save();
        settingsWindow?.ShowSaveResult(saved);
        if (!saved && !saveFailureNotified)
            tray?.ShowBalloonTip(5000, "SysPulse 配置未保存", preferences.SaveError ?? "无法写入唯一配置。", Forms.ToolTipIcon.Warning);
        saveFailureNotified = !saved;
    }
}

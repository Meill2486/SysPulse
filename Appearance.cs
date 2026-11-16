using System;
using System.Security;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SysPulse;

/// <summary>在系统设置事件发生时更新主题缓存，不在指标采样热路径访问注册表。</summary>
internal sealed class Appearance : IDisposable
{
    private const string Personalize = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private readonly Dispatcher dispatcher;
    private bool disposed;
    internal bool BarLight { get; private set; }
    internal bool AppsLight { get; private set; }
    internal event EventHandler? Changed;

    internal Appearance(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        Refresh();
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!dispatcher.HasShutdownStarted)
            dispatcher.BeginInvoke(DispatcherPriority.Background, Refresh);
    }

    private void Refresh()
    {
        if (disposed) return;
        try
        {
            BarLight = Registry.GetValue(Personalize, "SystemUsesLightTheme", 0) is 1;
            AppsLight = Registry.GetValue(Personalize, "AppsUseLightTheme", 0) is 1;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            Diagnostics.Record("主题", Diagnostics.Error(error));
        }
    }

    public void Dispose()
    {
        disposed = true;
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
    }
}

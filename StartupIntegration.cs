using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace SysPulse;

/// <summary>
/// 构造当前应用的启动命令，并管理当前用户的普通权限开机自启。
/// </summary>
internal static class StartupIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SysPulse";

    /// <summary>
    /// 同时支持 EXE 和 dotnet DLL 启动，提权与自启使用同一入口。
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(params string[] arguments)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。");
        var info = new ProcessStartInfo(executable) { UseShellExecute = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    /// <summary>
    /// 自启项包含带引号的绝对路径；移动程序后重新启用即可更新。
    /// </summary>
    internal static string CommandLine
    {
        get
        {
            var info = CreateStartInfo();
            string command = $"\"{info.FileName}\"";
            foreach (string argument in info.ArgumentList)
                command += $" \"{argument}\"";
            return command;
        }
    }

    /// <summary>
    /// 读取当前路径是否已注册自启。失败时返回原因，不把失败当作关闭。
    /// </summary>
    internal static bool TryRead(out bool enabled, out string? error)
    {
        enabled = false;
        error = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            enabled = string.Equals(key?.GetValue(ValueName) as string, CommandLine, StringComparison.OrdinalIgnoreCase);
            return true;
        }
        catch (Exception failure) when (failure is UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException)
        {
            error = $"无法读取开机自启：{failure.Message}";
            return false;
        }
    }

    /// <summary>
    /// 仅修改当前用户的自启项，写入失败时供界面恢复原选择并提示。
    /// </summary>
    internal static bool TrySet(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                key.SetValue(ValueName, CommandLine);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception failure) when (failure is UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException)
        {
            error = $"无法更改开机自启：{failure.Message}";
            return false;
        }
    }
}

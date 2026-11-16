using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SysPulse;

/// <summary>有界、按来源去重的内存诊断；仅由用户主动复制，不记录进程、路径或 IP。</summary>
internal static class Diagnostics
{
    private sealed record Entry(DateTimeOffset At, string State, long Count, DateTimeOffset? LastSuccess);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    /// <summary>更新一个来源的最近状态；限制键数和文本长度，不生成逐秒日志。</summary>
    internal static void Record(string source, string state, bool success = false)
    {
        lock (Gate)
        {
            if (!Entries.ContainsKey(source) && Entries.Count >= 64)
                Entries.Remove(Entries.MinBy(pair => pair.Value.At).Key);
            Entries.TryGetValue(source, out var previous);
            var now = DateTimeOffset.UtcNow;
            Entries[source] = new(now, state[..Math.Min(state.Length, 240)], (previous?.Count ?? 0) + 1,
                success ? now : previous?.LastSuccess);
        }
    }

    /// <summary>异常只保留类型和错误码，避免把完整文件路径等隐私带入诊断。</summary>
    internal static string Error(Exception error) => $"{error.GetType().Name} (0x{error.HResult:X8})";

    internal static string Export()
    {
        var text = new StringBuilder($"SysPulse {App.VersionLabel}\n{Environment.OSVersion}; x64={Environment.Is64BitProcess}\n");
        text.AppendLine($"活动逻辑处理器：{ProcessSampler.LogicalProcessors}；时间：{DateTimeOffset.Now:O}");
        lock (Gate)
            foreach (var (source, entry) in Entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                text.AppendLine($"{source}: {entry.State}; 次数={entry.Count}; UTC={entry.At:O}; 最后成功={entry.LastSuccess:O}");
        return text.ToString();
    }
}

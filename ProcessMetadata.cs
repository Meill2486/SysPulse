using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SysPulse;

/// <summary>Windows 创建时间与 PID 共同确定实例；不能取得创建时间时不建立跨帧身份。</summary>
internal readonly record struct ProcessIdentity(int Pid, long CreatedUtcTicks)
{
    internal string Key => $"instance:{Pid}:{CreatedUtcTicks}";
    internal bool IsValid => Pid > 0 && CreatedUtcTicks > 0;

    internal static ProcessIdentity? Read(Process process)
    {
        try
        {
            var identity = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks);
            return identity.IsValid ? identity : null;
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            Diagnostics.Record("进程身份不可用", Diagnostics.Error(error));
            return null;
        }
    }

    internal static bool IsUnavailable(Exception error) => error is InvalidOperationException or Win32Exception or
        NotSupportedException or UnauthorizedAccessException;
}

/// <summary>跨线程只传递不可变元数据与已经冻结的图像。</summary>
internal sealed record ProcessDetails(string Name, string? Path, ImageSource Icon);

/// <summary>单个按需工作线程；待办、实例与图标均有硬上限，失败按实例／路径记忆而不逐帧重试。</summary>
internal sealed class ProcessMetadata : IDisposable
{
    internal const int QueueCapacity = 256;
    internal const int InstanceCapacity = 4096;
    internal const int IconCapacity = 256;
    internal static ImageSource DefaultIcon { get; } = CreateDefaultIcon();
    private readonly object gate = new();
    private readonly Dictionary<ProcessIdentity, Entry> instances = new();
    private readonly Dictionary<string, CachedIcon> icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<Job> pending = new();
    private Thread? worker;
    private ExceptionDispatchInfo? fault;
    private long clock;
    private long generation;
    private bool active;
    private bool disposed;

    /// <summary>隐藏清空待办并使旧结果失效；已进入系统的调用只能等待返回，不能假称已取消。</summary>
    internal void SetActive(bool value)
    {
        lock (gate)
        {
            if (disposed || active == value) return;
            active = value;
            if (!value)
            {
                generation++;
                pending.Clear();
                foreach (var entry in instances.Values) entry.Queued = false;
            }
            Monitor.PulseAll(gate);
        }
    }

    internal void ThrowIfFaulted()
    {
        lock (gate) fault?.Throw();
    }

    /// <summary>只读缓存并尝试排队；此入口不读取 MainModule、文件版本或 Shell 图标。</summary>
    internal ProcessDetails Get(ProcessIdentity identity, string name)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            fault?.Throw();
            if (!instances.TryGetValue(identity, out var entry))
            {
                // 活动实例超过上限时只显示默认元数据；不靠逐帧驱逐／重读失败实例制造抖动。
                if (instances.Count >= InstanceCapacity) return new(name, null, DefaultIcon);
                instances[identity] = entry = new(identity, name);
            }
            if (active && !entry.Queued && !entry.InFlight && pending.Count < QueueCapacity && !entry.MetadataRead)
            {
                entry.Queued = true;
                pending.Enqueue(new(entry, generation));
                if (worker is null)
                {
                    worker = new Thread(Work) { IsBackground = true, Name = "SysPulse.ProcessMetadata" };
                    worker.Start();
                }
                Monitor.PulseAll(gate);
            }
            return Details(entry);
        }
    }

    /// <summary>退出实例只能读取旧身份缓存；绝不使用相同 PID 重新查询元数据。</summary>
    internal ProcessDetails Cached(ProcessIdentity identity, string name)
    {
        lock (gate)
            return instances.TryGetValue(identity, out var entry) ? Details(entry) : new(name, null, DefaultIcon);
    }

    /// <summary>退出实例短暂保留路径以接收迟到结算；与 ETW 相同的保留期和硬容量限制避免无限增长。</summary>
    internal void Retain(IReadOnlySet<ProcessIdentity> present)
    {
        long now = Stopwatch.GetTimestamp();
        lock (gate)
        {
            foreach (var (identity, entry) in instances.ToArray())
            {
                if (present.Contains(identity)) entry.MissingSince = null;
                else
                {
                    entry.MissingSince ??= now;
                    if (ProcessTrafficLedger.SettlementExpired(entry.MissingSince.Value, now)) instances.Remove(identity);
                }
            }
        }
    }

    private ProcessDetails Details(Entry entry)
    {
        ImageSource image = DefaultIcon;
        if (entry.Path is { } path && icons.TryGetValue(path, out var icon))
        {
            icon.Used = ++clock;
            image = icon.Image;
        }
        return new(entry.Name, entry.Path, image);
    }

    private bool Current(Job job)
    {
        lock (gate) return CurrentLocked(job);
    }

    private bool CurrentLocked(Job job) => !disposed && active && generation == job.Generation &&
        instances.TryGetValue(job.Entry.Identity, out var entry) && ReferenceEquals(entry, job.Entry);

    /// <summary>每次慢读之间检查需求代次；锁只用于调度和发布，绝不包围系统／Shell 调用。</summary>
    private void Work()
    {
        try
        {
            while (true)
            {
                Job job;
                lock (gate)
                {
                    while (!disposed && (!active || pending.Count == 0)) Monitor.Wait(gate);
                    if (disposed) return;
                    job = pending.Dequeue();
                    if (!CurrentLocked(job)) continue;
                    job.Entry.Queued = false;
                    job.Entry.InFlight = true;
                }
                try { Load(job); }
                finally { lock (gate) job.Entry.InFlight = false; }
            }
        }
        catch (Exception error)
        {
            // 只在跨线程边界暂存意外错误；下次采样原样重抛给 MonitorSession 的停止域。
            lock (gate)
            {
                fault = ExceptionDispatchInfo.Capture(error);
                pending.Clear();
                active = false;
            }
            Diagnostics.Record("进程元数据故障", Diagnostics.Error(error));
        }
    }

    private void Load(Job job)
    {
        var entry = job.Entry;
        string name = entry.Name;
        string? path = entry.Path;
        if (!entry.MetadataRead)
        {
            try
            {
                if (!Current(job)) return;
                using var process = Process.GetProcessById(entry.Identity.Pid);
                if (!Current(job)) return;
                if (ProcessIdentity.Read(process) == entry.Identity)
                {
                    if (!Current(job)) return;
                    string? candidate = NormalizePath(process.MainModule?.FileName);
                    if (!Current(job)) return;
                    // MainModule 可能按 PID 枚举模块，必须重新打开校验，不能依赖 StartTime 的旧缓存。
                    using var after = Process.GetProcessById(entry.Identity.Pid);
                    if (!Current(job)) return;
                    if (ProcessIdentity.Read(after) == entry.Identity) path = candidate;
                }
            }
            catch (Exception error) when (Expected(error))
            {
                path = null;
                Diagnostics.Record("进程元数据不可用", Diagnostics.Error(error));
            }
            if (path is not null && Current(job))
            {
                try
                {
                    string? description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (!string.IsNullOrWhiteSpace(description)) name = description.Trim();
                }
                catch (Exception error) when (Expected(error))
                {
                    Diagnostics.Record("进程显示名不可用", Diagnostics.Error(error));
                }
            }
        }
        if (!Current(job)) return;
        if (path is not null)
        {
            bool cached;
            lock (gate) cached = icons.ContainsKey(path);
            if (!cached && Current(job))
            {
                ImageSource image = DefaultIcon;
                try
                {
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                    if (icon is not null && Current(job))
                    {
                        var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                            BitmapSizeOptions.FromWidthAndHeight(16, 16));
                        bitmap.Freeze();
                        image = bitmap;
                    }
                }
                catch (Exception error) when (Expected(error))
                {
                    Diagnostics.Record("进程图标不可用", Diagnostics.Error(error));
                }
                lock (gate)
                {
                    if (!CurrentLocked(job)) return;
                    if (icons.Count >= IconCapacity) icons.Remove(icons.MinBy(pair => pair.Value.Used).Key);
                    icons[path] = new(image, ++clock);
                }
            }
        }
        lock (gate)
        {
            if (!CurrentLocked(job)) return;
            entry.Name = name;
            entry.Path = path;
            entry.MetadataRead = true;
        }
    }

    /// <summary>纯路径规范化：拒绝相对路径，统一扩展路径前缀和分隔符；比较采用 OrdinalIgnoreCase。</summary>
    internal static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return System.IO.Path.IsPathFullyQualified(path) ? System.IO.Path.GetFullPath(path) : null;
    }

    private static bool Expected(Exception error) => ProcessIdentity.IsUnavailable(error) ||
        error is ArgumentException or IOException or SecurityException or ExternalException;

    private static ImageSource CreateDefaultIcon()
    {
        var image = new DrawingImage(new GeometryDrawing(Brushes.SlateGray, null,
            Geometry.Parse("M2,2 L14,2 14,14 2,14 Z M4,4 L4,7 12,7 12,4 Z M4,9 L4,10 12,10 12,9 Z")));
        image.Freeze();
        return image;
    }

    /// <summary>唤醒唯一元数据线程并使在途任务失效。进程退出时允许慢速文件查询由系统回收。</summary>
    public void Dispose()
    {
        Thread? pendingWorker;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            active = false;
            generation++;
            pending.Clear();
            pendingWorker = worker;
            Monitor.PulseAll(gate);
        }
        // 该后台线程没有系统级会话。等待有上限，避免图标或版本读取拖住整个应用退出。
        if (pendingWorker?.Join(500) == false)
            Diagnostics.Record("进程元数据退出", "在途文件读取尚未返回，已停止接受结果");
        lock (gate)
        {
            instances.Clear();
            icons.Clear();
            worker = null;
        }
    }

    private sealed class Entry(ProcessIdentity identity, string name)
    {
        internal readonly ProcessIdentity Identity = identity;
        internal string Name = name;
        internal string? Path;
        internal bool Queued;
        internal bool InFlight;
        internal bool MetadataRead;
        internal long? MissingSince;
    }

    private sealed class CachedIcon(ImageSource image, long used)
    {
        internal readonly ImageSource Image = image;
        internal long Used = used;
    }

    private sealed record Job(Entry Entry, long Generation);
}

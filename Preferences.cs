using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SysPulse;

/// <summary>
/// 监控条内存和 CPU 的排版方式。
/// </summary>
internal enum BarStyle { Ring, Stack, Line }
/// <summary>
/// 监控条内可以独立排序的指标组。
/// </summary>
internal enum BarMetric { Network, Cpu, Memory }
/// <summary>
/// 写入 JSON 的指标顺序，沿用已发布版本的配置值。
/// </summary>
internal enum MetricOrder { NetworkCpuMemory, NetworkMemoryCpu, CpuNetworkMemory, CpuMemoryNetwork, MemoryNetworkCpu, MemoryCpuNetwork }
/// <summary>
/// 窗口采用跟随系统、深色或浅色配色的方式。
/// </summary>
internal enum ColorTheme { System, Dark, Light }

/// <summary>
/// 监控条上可选的 CPU 图标。
/// </summary>
internal enum CpuIconStyle { None, Chip, Pulse }

/// <summary>
/// 监控条上可选的内存图标。
/// </summary>
internal enum MemoryIconStyle { None, Module, Layers }

/// <summary>
/// 监控条上成对显示的下载和上传符号。
/// </summary>
internal enum NetworkSymbolStyle { Arrow, Chevron, Triangle }

/// <summary>
/// 保存当前有效设置；EXE 旁只有一个配置，加载失败时禁止自动覆盖。
/// </summary>
internal sealed class Preferences
{
    internal static readonly string ConfigurationPath = Path.Combine(
        Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet" ? AppContext.BaseDirectory :
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "SysPulse.json");
    internal string FilePath { get; private set; } = ConfigurationPath;
    internal string? LoadError { get; private set; }
    internal string? SaveError { get; private set; }
    internal bool SaveBlocked => LoadError is not null;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public bool ShowDownload { get; set; } = true;
    public bool ShowUpload { get; set; } = true;
    public bool ShowCpu { get; set; } = true;
    public bool ShowMemory { get; set; } = true;
    public string CpuLabel { get; set; } = "CPU";
    public string MemLabel { get; set; } = "内存";
    public BarStyle BarStyle { get; set; }
    public MetricOrder MetricOrder { get; set; } = MetricOrder.MemoryNetworkCpu;
    public int RefreshMs { get; set; } = 1000;
    public int FontSize { get; set; } = 12;
    public int BarOpacity { get; set; }
    public ColorTheme Theme { get; set; }
    public CpuIconStyle CpuIcon { get; set; }
    public MemoryIconStyle MemoryIcon { get; set; }
    public NetworkSymbolStyle NetworkSymbols { get; set; }
    /// <summary>
    /// 相对任务栏起点的偏移量，单位为 DIP。<c>-1</c> 表示跟随通知区域。
    /// </summary>
    public int BarOffset { get; set; } = -1;
    public bool BarVisible { get; set; } = true;
    public bool ElevateOnStart { get; set; }
    public string SortKey { get; set; } = "down";
    public bool SortAscending { get; set; }
    public bool ProcessCompact { get; set; }
    public Guid? NetworkInterface { get; set; }
    public int CpuWarn { get; set; } = 70;
    public int CpuAlert { get; set; } = 90;
    public int MemWarn { get; set; } = 70;
    public int MemAlert { get; set; } = 90;
    public int DiskWarn { get; set; } = 15;
    public int DiskAlert { get; set; } = 5;
    public int NetWarn { get; set; } = 5;
    public int NetAlert { get; set; } = 20;

    /// <summary>
    /// 将保存的顺序还原为列表和监控条共用的三个指标组。
    /// </summary>
    public (BarMetric First, BarMetric Second, BarMetric Third) GetMetricOrder() => DecodeMetricOrder(MetricOrder);

    /// <summary>
    /// 判断指标组是否启用。任一方向开启时，流量作为一组参与排序。
    /// </summary>
    public bool IsMetricEnabled(BarMetric metric) => metric switch
    {
        BarMetric.Network => ShowDownload || ShowUpload,
        BarMetric.Cpu => ShowCpu,
        BarMetric.Memory => ShowMemory,
        _ => false
    };

    /// <summary>
    /// 将启用项按新顺序填回原槽位，保留未启用项的位置与旧配置格式。
    /// </summary>
    public void SetEnabledMetricOrder(IReadOnlyList<BarMetric> enabled)
    {
        var (first, second, third) = GetMetricOrder();
        int index = 0;
        BarMetric Merge(BarMetric metric) => IsMetricEnabled(metric) ? enabled[index++] : metric;
        var order = (Merge(first), Merge(second), Merge(third));
        MetricOrder = Enum.GetValues<MetricOrder>().First(value => DecodeMetricOrder(value) == order);
    }

    private static (BarMetric, BarMetric, BarMetric) DecodeMetricOrder(MetricOrder order) => order switch
    {
        MetricOrder.NetworkCpuMemory => (BarMetric.Network, BarMetric.Cpu, BarMetric.Memory),
        MetricOrder.NetworkMemoryCpu => (BarMetric.Network, BarMetric.Memory, BarMetric.Cpu),
        MetricOrder.CpuNetworkMemory => (BarMetric.Cpu, BarMetric.Network, BarMetric.Memory),
        MetricOrder.CpuMemoryNetwork => (BarMetric.Cpu, BarMetric.Memory, BarMetric.Network),
        MetricOrder.MemoryNetworkCpu => (BarMetric.Memory, BarMetric.Network, BarMetric.Cpu),
        MetricOrder.MemoryCpuNetwork => (BarMetric.Memory, BarMetric.Cpu, BarMetric.Network),
        _ => (BarMetric.Memory, BarMetric.Network, BarMetric.Cpu)
    };

    /// <summary>只读取指定文件；损坏或不可读时使用临时默认设置并阻止自动覆盖原文件。</summary>
    public static Preferences Load() => Load(ConfigurationPath);

    internal static Preferences Load(string path)
    {
        try
        {
            var value = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path), Json)
                ?? throw new JsonException("配置根节点不能为 null");
            value.FilePath = path;
            value.Normalize();
            return value;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new Preferences { FilePath = path };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            string state = "无法读取配置，原文件已保留；自动保存已暂停。" + Diagnostics.Error(error);
            Diagnostics.Record("配置加载", state);
            return new Preferences { FilePath = path, LoadError = state };
        }
    }

    /// <summary>同目录随机临时文件写完后原子替换；不可写时反馈，不另存到其他目录。</summary>
    public bool Save() => Save(FilePath);

    internal bool Save(string path)
    {
        if (SaveBlocked) { SaveError = LoadError; return false; }
        return TrySave(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>只在用户明确确认后调用：先备份原文件，再用当前界面设置重建配置。</summary>
    internal bool Rebuild()
    {
        try
        {
            if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + $".{DateTime.Now:yyyyMMddHHmmss}.{Guid.NewGuid():N}.bak");
            if (!TrySave(FilePath, JsonSerializer.Serialize(this, Json))) return false;
            LoadError = null;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SaveError = "无法备份或重建配置：" + Diagnostics.Error(error);
            Diagnostics.Record("配置保存", SaveError);
            return false;
        }
    }

    private bool TrySave(string path, string content)
    {
        string directory = Path.GetDirectoryName(path)!;
        string temp = Path.Combine(directory, Path.GetRandomFileName());
        bool created = false;
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                using var writer = new StreamWriter(stream, leaveOpen: true);
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            SaveError = null;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SaveError = "设置已应用，但无法写入 EXE 旁配置：" + Diagnostics.Error(error);
            Diagnostics.Record("配置保存", SaveError);
            return false;
        }
        finally
        {
            try { if (created && File.Exists(temp)) File.Delete(temp); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Diagnostics.Record("配置临时文件清理", Diagnostics.Error(error)); }
        }
    }

    /// <summary>
    /// 修正未知的枚举值、超范围设置及彼此交叉的告警阈值。
    /// </summary>
    private void Normalize()
    {
        CpuLabel = LimitLabel(CpuLabel);
        MemLabel = LimitLabel(MemLabel);
        if (!Enum.IsDefined(BarStyle)) BarStyle = BarStyle.Ring;
        if (!Enum.IsDefined(MetricOrder)) MetricOrder = MetricOrder.MemoryNetworkCpu;
        if (!Enum.IsDefined(Theme)) Theme = ColorTheme.System;
        if (!Enum.IsDefined(CpuIcon)) CpuIcon = CpuIconStyle.None;
        if (!Enum.IsDefined(MemoryIcon)) MemoryIcon = MemoryIconStyle.None;
        if (!Enum.IsDefined(NetworkSymbols)) NetworkSymbols = NetworkSymbolStyle.Arrow;
        RefreshMs = Math.Clamp(RefreshMs, 500, 10000);
        FontSize = Math.Clamp(FontSize, 11, 16);
        BarOpacity = Math.Clamp(BarOpacity, 0, 100);
        BarOffset = Math.Max(-1, BarOffset);
        if (SortKey is not ("name" or "cpu" or "mem" or "down" or "up") ||
            (ProcessCompact && SortKey is "cpu" or "mem"))
        {
            SortKey = "down";
            SortAscending = false;
        }
        CpuWarn = Math.Clamp(CpuWarn, 1, 99);
        CpuAlert = Math.Clamp(CpuAlert, CpuWarn + 1, 100);
        MemWarn = Math.Clamp(MemWarn, 1, 99);
        MemAlert = Math.Clamp(MemAlert, MemWarn + 1, 100);
        DiskWarn = Math.Clamp(DiskWarn, 2, 98);
        DiskAlert = Math.Clamp(DiskAlert, 1, DiskWarn - 1);
        NetWarn = Math.Clamp(NetWarn, 1, 200);
        NetAlert = Math.Clamp(NetAlert, NetWarn + 1, 500);
    }

    private static string LimitLabel(string? label) => label is null ? "" : label[..Math.Min(6, label.Length)];
}

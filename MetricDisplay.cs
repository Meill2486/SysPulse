using System;
using System.Globalization;
using System.Windows.Media;

namespace SysPulse;

/// <summary>
/// 统一监控条和面板的数值格式、状态阈值与调色板。
/// </summary>
internal sealed class MetricDisplay(string normal, string warning, string alert, string upload, string foreground, string track)
{
    public static readonly MetricDisplay Light = new("#1B642D", "#795000", "#AC2232", "#075A8D", "#383C42", "#54717B83");
    public static readonly MetricDisplay Dark = new("#90E4A3", "#FFD47B", "#FF9B9F", "#94CFFF", "#D3D8DE", "#547D8994");
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB"];

    public Brush Normal { get; } = MakeBrush(normal);
    public Brush Warning { get; } = MakeBrush(warning);
    public Brush Alert { get; } = MakeBrush(alert);
    public Brush Upload { get; } = MakeBrush(upload);
    public Brush Foreground { get; } = MakeBrush(foreground);
    public Brush Track { get; } = MakeBrush(track);

    /// <summary>
    /// 按提示和告警阈值选择颜色。磁盘可用率等越低越紧张的指标采用反向判断。
    /// </summary>
    public Brush Status(double? value, int warn, int alert, bool reverse = false)
    {
        if (value is not double number)
            return Foreground;
        if (reverse ? number < alert : number >= alert)
            return Alert;
        return (reverse ? number < warn : number >= warn) ? Warning : Normal;
    }

    /// <summary>
    /// 平时分别保留上下行颜色，合计速率达到阈值时统一使用告警等级对应的颜色。
    /// </summary>
    public (Brush Download, Brush Upload) Network(SystemSample sample, Preferences settings)
    {
        double? combined = (sample.Download + sample.Upload) / (1024 * 1024);
        var status = Status(combined, settings.NetWarn, settings.NetAlert);
        return status == Normal || status == Foreground
            ? (sample.Download.HasValue ? Normal : Foreground, sample.Upload.HasValue ? Upload : Foreground)
            : (status, status);
    }

    /// <summary>
    /// 将百分比显示为整数。缺少样本时显示破折号。
    /// </summary>
    public static string Percent(double? value) => value is double percent ? $"{percent:0}%" : "—";

    /// <summary>
    /// 格式化 1024 进制字节数或速率，缺失和非有限值不显示为有效读数。
    /// </summary>
    public static string Bytes(double? value, bool rate = false)
    {
        if (value is not double amount || !double.IsFinite(amount) || amount < 0)
            return "—";

        int unit = 0;
        while (amount >= 1024 && unit < Units.Length - 1)
        {
            amount /= 1024;
            unit++;
        }

        string number = amount.ToString(unit > 0 && amount < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);
        return $"{number} {Units[unit]}{(rate ? "/s" : "")}";
    }

    /// <summary>
    /// 从颜色值创建可安全复用的冻结画刷。
    /// </summary>
    public static SolidColorBrush MakeBrush(string text)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text));
        brush.Freeze();
        return brush;
    }
}

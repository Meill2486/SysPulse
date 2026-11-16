using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace SysPulse;

/// <summary>仅保存已有整机采样的四项短时指标，间隔和网络段用于绘制缺口。</summary>
internal readonly record struct TrendPoint(long Timestamp, double? Cpu, double? Memory, double? Download,
    double? Upload, int NetworkSegment, int RefreshMs);

/// <summary>60 秒固定上限环形历史；由采样域持有，调用方使用同一把锁读写。</summary>
internal sealed class TrendHistory
{
    private readonly TrendPoint[] points = new TrendPoint[122];
    private int first, count;
    internal void Add(TrendPoint point)
    {
        if (count > 0 && point.Timestamp <= points[(first + count - 1) % points.Length].Timestamp) return;
        while (count > 0 && Stopwatch.GetElapsedTime(points[first].Timestamp, point.Timestamp).TotalSeconds > 60)
        { first = (first + 1) % points.Length; count--; }
        if (count == points.Length) { first = (first + 1) % points.Length; count--; }
        points[(first + count++) % points.Length] = point;
    }
    internal TrendPoint[] Snapshot()
    {
        var result = new TrendPoint[count];
        for (int i = 0; i < count; i++) result[i] = points[(first + i) % points.Length];
        return result;
    }
    internal void Clear() => first = count = 0;
}

internal enum TrendMetric { Cpu, Memory, Download, Upload }

/// <summary>轻量静态折线，不插值缺样、不平滑尖峰、不使用动画或独立计时器。</summary>
public sealed class MiniChart : FrameworkElement
{
    private TrendPoint[] points = [];
    private TrendMetric metric;
    private Pen? pen;

    internal void SetData(TrendPoint[] samples, TrendMetric series, Brush color)
    {
        points = samples;
        metric = series;
        if (pen?.Brush != color) { pen = new Pen(color, 1.4); pen.Freeze(); }
        InvalidateVisual();
    }

    private double? Value(TrendPoint point) => metric switch
    {
        TrendMetric.Cpu => point.Cpu, TrendMetric.Memory => point.Memory,
        TrendMetric.Download => point.Download, _ => point.Upload
    };

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (points.Length < 2 || ActualWidth <= 0 || ActualHeight <= 0 || pen is null) return;
        bool network = metric is TrendMetric.Download or TrendMetric.Upload;
        double maximum = network ? 1 : 100;
        if (network)
            foreach (var point in points)
                if (Value(point) is double number && double.IsFinite(number) && number >= 0) maximum = Math.Max(maximum, number);
        long end = Stopwatch.GetTimestamp();
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            bool connected = false;
            TrendPoint previous = default;
            foreach (var point in points)
            {
                if (Value(point) is not double number || !double.IsFinite(number) || number < 0)
                { connected = false; continue; }
                double age = Stopwatch.GetElapsedTime(point.Timestamp, end).TotalSeconds;
                if (age > 60) continue;
                bool gap = connected && (Stopwatch.GetElapsedTime(previous.Timestamp, point.Timestamp).TotalMilliseconds >
                    Math.Max(1500, 2.5 * Math.Max(point.RefreshMs, previous.RefreshMs)) ||
                    (network && point.NetworkSegment != previous.NetworkSegment));
                var position = new Point(ActualWidth * (1 - age / 60), 1 + (ActualHeight - 2) * (1 - Math.Clamp(number / maximum, 0, 1)));
                if (!connected || gap) path.BeginFigure(position, false, false); else path.LineTo(position, true, false);
                connected = true;
                previous = point;
            }
        }
        geometry.Freeze();
        drawing.DrawGeometry(null, pen, geometry);
    }
}

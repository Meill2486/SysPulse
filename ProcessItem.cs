using System;
using System.ComponentModel;
using System.Windows.Media;

namespace SysPulse;

/// <summary>
/// 界面线程持有的稳定进程行，只为显示内容真正变化的属性发送通知。
/// </summary>
internal sealed class ProcessItem(ProcessRow sample) : INotifyPropertyChanged
{
    private ProcessRow sample = sample;
    public string Name => sample.Name;
    public string Key => sample.DisplayKey;
    internal string InstanceKey => sample.Key;
    public string? Path => sample.Path;
    public System.Collections.Generic.IReadOnlyList<int> Pids => sample.Pids;
    public string DisplayName => sample.DisplayName;
    public string CpuText => sample.CpuText;
    public string MemoryText => sample.MemoryText;
    public string DownloadText => sample.DownloadText;
    public string UploadText => sample.UploadText;
    public ImageSource? Icon => sample.Icon;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 在不替换行对象的前提下接收新快照，保留选择和已实现的单元格。
    /// </summary>
    internal void Update(ProcessRow next)
    {
        var previous = sample;
        sample = next;
        void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        if (previous.DisplayName != next.DisplayName) Changed(nameof(DisplayName));
        if (previous.Path != next.Path) Changed(nameof(Path));
        if (previous.Name != next.Name) Changed(nameof(Name));
        if (previous.CpuText != next.CpuText) Changed(nameof(CpuText));
        if (previous.MemoryText != next.MemoryText) Changed(nameof(MemoryText));
        if (previous.DownloadText != next.DownloadText) Changed(nameof(DownloadText));
        if (previous.UploadText != next.UploadText) Changed(nameof(UploadText));
        if (!ReferenceEquals(previous.Icon, next.Icon)) Changed(nameof(Icon));
    }
}

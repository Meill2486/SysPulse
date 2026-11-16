using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SysPulse;

/// <summary>
/// 用于面板的线性矢量图标，描边颜色取自控件前景色。
/// </summary>
public sealed class PanelIcon : Control
{
    /// <summary>
    /// 标识图标路径所用的 WPF 依赖属性。
    /// </summary>
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(PanelIcon), new PropertyMetadata(null));

    /// <summary>
    /// 图标的矢量路径。
    /// </summary>
    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>
    /// 按共享资源键和尺寸创建图标控件。
    /// </summary>
    internal static PanelIcon Create(string key, int size = 16) => new()
    {
        Data = (Geometry)Application.Current.FindResource(key),
        Width = size,
        Height = size
    };
}

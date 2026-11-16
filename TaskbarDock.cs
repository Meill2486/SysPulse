using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace SysPulse;

/// <summary>
/// 主屏任务栏的窗口句柄、物理像素边界和 DPI 缩放。
/// </summary>
internal readonly record struct TaskbarFrame(nint Handle, Rectangle Bounds, Rectangle Tray, double Scale)
{
    /// <summary>
    /// 判断任务栏本体是否发生变化，允许单独更新通知区域宽度。
    /// </summary>
    public bool SameLayout(TaskbarFrame other) => Handle == other.Handle && Bounds == other.Bounds && Scale == other.Scale;
}

/// <summary>
/// 封装 Explorer 任务栏的几何探测、窗口层级与原生菜单操作。
/// </summary>
internal static class TaskbarDock
{
    private const int ExtendedStyle = -20;
    private const int NoActivate = 0x08000000;
    private const int ToolWindow = 0x00000080;
    private const int Topmost = 0x00000008;
    private const uint PreviousWindow = 3;

    /// <summary>
    /// 读取当前可停靠的主屏任务栏。任务栏收起或前台全屏时返回空值。
    /// </summary>
    public static TaskbarFrame? ReadFrame()
    {
        nint bar = FindWindow("Shell_TrayWnd", null);
        var screen = new Rectangle(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));
        var bounds = ReadBounds(bar);
        double scale = GetDpiForWindow(bar) / 96d;

        // 自动隐藏时外框会移出屏幕或只剩边缘，必须等完整栏体出现。
        if (bounds.IsEmpty || bounds.Width <= bounds.Height || !screen.Contains(bounds) || scale <= 0 ||
            bounds.Height < 24 * scale || IsFullScreen(screen, bar))
            return null;

        var tray = ReadBounds(FindWindowEx(bar, 0, "TrayNotifyWnd", null));
        if (tray.Width <= 0 || tray.Height <= 0 || !bounds.Contains(tray))
            tray = Rectangle.Empty;
        return new TaskbarFrame(bar, bounds, tray, scale);
    }

    /// <summary>
    /// 通过 UI Automation 获取已占用控件的最远边界。无法确认开始按钮时返回空值。
    /// </summary>
    public static int? ReadOccupiedEnd(TaskbarFrame frame, AutomationElement root)
    {
        try
        {
            var occupied = new OrCondition(
                IsControl(ControlType.Button), IsControl(ControlType.SplitButton),
                IsControl(ControlType.Edit),
                IsControl(ControlType.CheckBox), IsControl(ControlType.RadioButton),
                IsControl(ControlType.ComboBox), IsControl(ControlType.ListItem),
                IsControl(ControlType.TabItem), IsControl(ControlType.Hyperlink));

            var request = new CacheRequest { AutomationElementMode = AutomationElementMode.None };
            request.Add(AutomationElement.BoundingRectangleProperty);
            request.Add(AutomationElement.AutomationIdProperty);
            request.Add(AutomationElement.ClassNameProperty);
            request.Add(AutomationElement.IsOffscreenProperty);
            request.Add(AutomationElement.ProcessIdProperty);
            AutomationElementCollection controls;
            using (request.Activate())
                controls = root.FindAll(TreeScope.Descendants, occupied);

            bool foundStart = false;
            int occupiedEnd = frame.Bounds.Left;
            int trayStart = frame.Tray.Left;

            foreach (AutomationElement control in controls)
            {
                var item = control.Cached;
                var rect = item.BoundingRectangle;
                if (!IsLayoutControl(item.ClassName, item.ProcessId) || item.IsOffscreen || rect.IsEmpty ||
                    rect.Right <= frame.Bounds.Left || rect.Left >= frame.Bounds.Right ||
                    rect.Bottom <= frame.Bounds.Top || rect.Top >= frame.Bounds.Bottom)
                    continue;

                // 弹出菜单与浮层可以碰到栏边，但不属于任务栏一排按钮的布局。
                if (rect.Top < frame.Bounds.Top - 1 || rect.Bottom > frame.Bounds.Bottom + 1)
                    continue;

                if (item.AutomationId == "StartButton" || item.ClassName == "Start")
                    foundStart = true;

                double start = rect.Left;
                double end = rect.Right;
                if (start < trayStart)
                    occupiedEnd = Math.Max(occupiedEnd, (int)Math.Ceiling(end));
            }

            return foundStart ? occupiedEnd : null;
        }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException
                                      or COMException or UnauthorizedAccessException)
        {
            // Explorer 正在重建或尚未提供可用的布局，下次再试。
            return null;
        }
    }

    /// <summary>通知区动画可越过托盘左缘；通知控件和本程序的 owned 窗口均不占应用按钮空位。</summary>
    internal static bool IsLayoutControl(string className, int processId) =>
        processId != Environment.ProcessId && !className.StartsWith("SystemTray.", StringComparison.Ordinal);

    /// <summary>
    /// 计算已占用控件与通知区域之间的安全空位，并在两端预留间距。
    /// </summary>
    public static Rectangle FreeSlot(TaskbarFrame frame, int occupiedEnd)
    {
        int gap = (int)Math.Ceiling(4 * frame.Scale);
        int begin = occupiedEnd + gap;
        int limit = frame.Tray.Left - gap;
        if (limit <= begin)
            return Rectangle.Empty;

        return Rectangle.FromLTRB(begin, frame.Bounds.Top, limit, frame.Bounds.Bottom);
    }

    /// <summary>
    /// 将窗口设置为点击时不夺取前台焦点的工具窗口。
    /// </summary>
    public static void MakeNonActivating(nint window) =>
        SetWindowLong(window, ExtendedStyle, GetWindowLong(window, ExtendedStyle) | NoActivate | ToolWindow);

    public static void CapturePointer(nint window) => SetCapture(window);
    public static bool HasPointerCapture(nint window) => GetCapture() == window;
    public static void ReleasePointer() => ReleaseCapture();
    /// <summary>
    /// 按物理像素同时更新窗口位置和尺寸，保留焦点与层级。
    /// </summary>
    public static void Move(nint window, Rectangle bounds) =>
        SetWindowPos(window, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010 | 0x0200 | 0x0004);

    /// <summary>
    /// 拖动时只移动窗口，保留尺寸、焦点与层级。
    /// </summary>
    public static void Move(nint window, Point position) =>
        SetWindowPos(window, 0, position.X, position.Y, 0, 0, 0x0010 | 0x0200 | 0x0004 | 0x0001);

    /// <summary>
    /// 更新窗口边界，必要时将窗口恢复到任务栏正上方。
    /// </summary>
    public static void Place(nint window, Rectangle bounds, nint taskbar)
    {
        bool needsTop = false;
        // 只在任务栏确实压住监控条时纠正层级，避免反复压住系统菜单。
        for (nint above = GetWindow(window, PreviousWindow); above != 0; above = GetWindow(above, PreviousWindow))
        {
            if (above == taskbar)
            {
                needsTop = true;
                break;
            }
        }

        bool sameBounds = ReadBounds(window) == bounds;
        if (!needsTop && sameBounds)
            return;

        // 插在任务栏紧上方，保留已在其上方的系统菜单和浮层的顺序。
        nint predecessor = needsTop ? GetWindow(taskbar, PreviousWindow) : 0;
        nint insertAfter = predecessor != 0 && (GetWindowLong(predecessor, ExtendedStyle) & Topmost) != 0
            ? predecessor : new nint(-1);
        uint flags = 0x0010 | 0x0200; // SWP_NOACTIVATE | SWP_NOOWNERZORDER
        if (sameBounds)
            flags |= 0x0001 | 0x0002; // 只调整层级，不触发窗口移位或缩放。
        if (!needsTop)
            flags |= 0x0004; // SWP_NOZORDER
        SetWindowPos(window, insertAfter, bounds.X, bounds.Y, bounds.Width, bounds.Height, flags);
    }

    /// <summary>
    /// 在任务栏内侧打开原生菜单并返回选中的命令编号。
    /// </summary>
    public static uint ShowBarMenu(nint window, TaskbarFrame taskbar, int x, int y, Action restoreLayer)
    {
        nint menu = CreatePopupMenu();
        if (menu == 0)
            return 0;

        nint previousForeground = GetForegroundWindow();
        try
        {
            AppendMenu(menu, 0, 3, "设置");
            AppendMenu(menu, 0, 4, "停靠到托盘前");
            AppendMenu(menu, 0, 1, "隐藏监控条");
            AppendMenu(menu, 0, 2, "退出 SysPulse");
            // 原生菜单跟踪需要前台所有者，点外部和 Esc 才能正常结束。
            if (!SetForegroundWindow(window) && GetForegroundWindow() != window)
                return 0;

            restoreLayer();
            const uint returnCommand = 0x0100 | 0x0080 | 0x0002; // RETURNCMD | NONOTIFY | RIGHTBUTTON
            var bounds = taskbar.Bounds;
            bool onBottom = bounds.Top >= GetSystemMetrics(1) / 2;
            y = onBottom ? bounds.Top : bounds.Bottom;
            uint align = onBottom ? 0x0020u : 0; // TPM_BOTTOMALIGN

            return TrackPopupMenuEx(menu, returnCommand | align, x, y, window, 0);
        }
        finally
        {
            DestroyMenu(menu);
            PostMessage(window, 0, 0, 0); // WM_NULL，确保后续右键能正常弹出。
            if (previousForeground != 0 && previousForeground != window && GetForegroundWindow() == window)
                SetForegroundWindow(previousForeground);
        }
    }

    private static PropertyCondition IsControl(ControlType type) =>
        new(AutomationElement.ControlTypeProperty, type);

    /// <summary>
    /// 将面板贴近监控条，并把面板位置限制在主屏范围内。
    /// </summary>
    public static Rectangle NextToBar(TaskbarFrame frame, Rectangle bar, int width, int height)
    {
        int screenWidth = GetSystemMetrics(0);
        int screenHeight = GetSystemMetrics(1);
        int gap = (int)Math.Ceiling(6 * frame.Scale);
        int x = bar.Right - width;
        int y = bar.Top >= screenHeight / 2 ? bar.Top - height - gap : bar.Bottom + gap;
        return new Rectangle(Math.Clamp(x, 0, Math.Max(0, screenWidth - width)),
            Math.Clamp(y, 0, Math.Max(0, screenHeight - height)), width, height);
    }

    /// <summary>返回概览所在任务栏内侧的主屏工作区，边界均为物理像素。</summary>
    internal static Rectangle OverviewArea(TaskbarFrame frame, Rectangle bar)
    {
        var area = PrimaryWorkArea();
        int gap = (int)Math.Ceiling(6 * frame.Scale);
        if (bar.Top >= GetSystemMetrics(1) / 2)
            return Rectangle.FromLTRB(area.Left, area.Top, area.Right, Math.Clamp(bar.Top - gap, area.Top + 1, area.Bottom));
        return Rectangle.FromLTRB(area.Left, Math.Clamp(bar.Bottom + gap, area.Top, area.Bottom - 1), area.Right, area.Bottom);
    }

    /// <summary>
    /// 无监控条锚点时按工作区边界计算面板位置，调整大小时优先保留右下角。
    /// </summary>
    public static Rectangle ResizeInWorkArea(nint window, int width, int height, bool firstOpen)
    {
        double scale = GetDpiForWindow(window) / 96d;
        width = (int)Math.Ceiling(width * scale);
        height = (int)Math.Ceiling(height * scale);
        var area = PrimaryWorkArea();
        var previous = ReadBounds(window);
        int right = firstOpen ? area.Right - 12 : previous.Right;
        int bottom = firstOpen ? area.Bottom - 12 : previous.Bottom;
        return new Rectangle(Math.Clamp(right - width, area.Left, Math.Max(area.Left, area.Right - width)),
            Math.Clamp(bottom - height, area.Top, Math.Max(area.Top, area.Bottom - height)), width, height);
    }

    private static Rectangle PrimaryWorkArea() => SystemParametersInfo(0x0030, 0, out var workArea, 0) &&
        workArea.Right > workArea.Left && workArea.Bottom > workArea.Top // SPI_GETWORKAREA
        ? workArea.Rectangle : new Rectangle(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));

    /// <summary>
    /// 稀疏采样监控条相邻空位的亮度，为自动配色提供依据。
    /// </summary>
    public static double? AdjacentBrightness(Rectangle bar, Rectangle slot)
    {
        int first = slot.Left;
        int before = bar.Left;
        int after = bar.Right;
        int last = slot.Right;
        int start = before - first >= 8 ? Math.Max(first, before - 22) : after + 2;
        int end = before - first >= 8 ? before - 2 : Math.Min(last, after + 22);
        if (end - start < 4)
            return null;

        int crossCenter = (bar.Top + bar.Bottom) / 2;
        int half = Math.Min(20, bar.Height / 2);
        nint dc = GetDC(0);
        if (dc == 0)
            return null;
        try
        {
            // 稀疏采样相邻空位，每次最多二十个像素，避开监控条自己的窗口。
            double sum = 0;
            int count = 0;
            for (int col = 0; col < 5; col++)
            for (int row = 0; row < 4; row++)
            {
                int along = start + (2 * col + 1) * (end - start) / 10;
                int cross = crossCenter - half + (2 * row + 1) * half / 4;
                uint color = GetPixel(dc, along, cross);
                if (color == uint.MaxValue)
                    continue;
                sum += .2126 * (color & 255) + .7152 * ((color >> 8) & 255) + .0722 * ((color >> 16) & 255);
                count++;
            }
            return count > 0 ? sum / (count * 255) : null;
        }
        finally
        {
            ReleaseDC(0, dc);
        }
    }

    /// <summary>检查主屏前台应用是否全屏；菜单补位也必须先让位。</summary>
    public static bool HasForegroundFullScreen() =>
        IsFullScreen(new Rectangle(0, 0, GetSystemMetrics(0), GetSystemMetrics(1)), FindWindow("Shell_TrayWnd", null));

    /// <summary>按物理像素判断屏幕覆盖，容忍窗口切换和 DWM 边界的两像素舍入误差。</summary>
    internal static bool CoversScreen(Rectangle bounds, Rectangle screen) =>
        !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0 &&
        bounds.Left <= screen.Left + 2 && bounds.Top <= screen.Top + 2 &&
        bounds.Right >= screen.Right - 2 && bounds.Bottom >= screen.Bottom - 2;

    /// <summary>只检查前台应用，避免后台全屏窗口阻止用户唤出任务栏。</summary>
    private static bool IsFullScreen(Rectangle screen, nint taskbar)
    {
        nint window = GetForegroundWindow();
        if (window == 0 || window == taskbar || window == GetShellWindow() ||
            GetAncestor(window, 3) == taskbar) // GA_ROOTOWNER，任务栏弹层不是全屏应用。
            return false;

        var bounds = DwmGetWindowAttribute(window, 9, out var frame, Marshal.SizeOf<NativeRect>()) == 0
            ? frame.Rectangle : ReadBounds(window);

        // 普通最大化的外框可能铺满屏幕，客户区仍留有标题栏；不能仅凭样式排除真正全屏。
        const int caption = 0x00C00000;
        int style = GetWindowLong(window, -16);
        if ((style & caption) != caption)
            bounds = ReadBounds(window); // 无标题栏全屏仍可能有不可见缩放边框，DWM 外框会向内收缩。
        else if (IsZoomed(window))
        {
            var origin = new Point();
            if (!GetClientRect(window, out var client) || !ClientToScreen(window, ref origin))
                return false;
            bounds = new Rectangle(origin, client.Rectangle.Size);
        }
        if (!CoversScreen(bounds, screen))
            return false;

        // 桌面宿主和托盘浮层也可能铺满屏幕，不能按几何边界当作全屏应用。
        var className = new StringBuilder(64);
        GetClassName(window, className, className.Capacity);
        string windowClass = className.ToString();
        if (windowClass == "TXMiniSkin")
        {
            // 腾讯桌面整理的宿主会覆盖几乎整屏；只排除该宿主，不排除同类的其他窗口。
            var title = new StringBuilder(64);
            GetWindowText(window, title, title.Capacity);
            if (title.ToString() == "桌面整理")
                return false;
        }
        return windowClass is not ("Progman" or "WorkerW" or
            "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow");
    }

    private static Rectangle ReadBounds(nint window) =>
        window != 0 && GetWindowRect(window, out var rect) ? rect.Rectangle : Rectangle.Empty;

    /// <summary>
    /// 监听前台切换、前台窗口尺寸变化和窗口重排，及时检查全屏与任务栏层级。
    /// </summary>
    internal sealed class LayerEvents : IDisposable
    {
        private const uint Foreground = 0x0003;
        private const uint Reorder = 0x8004;
        private const uint LocationChange = 0x800B;
        private readonly WinEventCallback callback;
        private readonly nint foregroundHook;
        private readonly nint reorderHook;
        private readonly nint locationHook;

        public LayerEvents(Action<bool> changed)
        {
            nint desktop = GetDesktopWindow();
            callback = (_, eventId, window, objectId, childId, _, _) =>
            {
                nint taskbar = FindWindow("Shell_TrayWnd", null);
                if (eventId == Foreground ||
                    (eventId == LocationChange && objectId == 0 && childId == 0 && window == GetForegroundWindow()) ||
                    (eventId == Reorder && childId == 0 && (objectId is 0 or -4) && (window == desktop || window == 0 || window == taskbar)))
                    changed(false);
                if (eventId == LocationChange && window == taskbar && childId == 0 && objectId == 0)
                    changed(true);
            };
            const uint skipOwnProcess = 2; // OUTOFCONTEXT | SKIPOWNPROCESS
            foregroundHook = SetWinEventHook(Foreground, Foreground, 0, callback, 0, 0, skipOwnProcess);
            reorderHook = SetWinEventHook(Reorder, Reorder, 0, callback, 0, 0, skipOwnProcess);
            locationHook = SetWinEventHook(LocationChange, LocationChange, 0, callback, 0, 0, skipOwnProcess);
        }

        public void Dispose()
        {
            if (foregroundHook != 0)
                UnhookWinEvent(foregroundHook);
            if (reorderHook != 0)
                UnhookWinEvent(reorderHook);
            if (locationHook != 0)
                UnhookWinEvent(locationHook);
        }
    }

    private delegate void WinEventCallback(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle Rectangle => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    internal static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int capacity);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder title, int capacity);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out NativeRect rect, uint flags);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")]
    private static extern nint GetCapture();
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();
    [DllImport("user32.dll")]
    private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint item, string text);
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventCallback callback,
        uint process, uint thread, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(nint dc, int x, int y);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, uint attribute, out NativeRect rect, int size);
}

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace SysPulse;

/// <summary>
/// 在浮层显示期间观察点外和 Esc，原目标窗口仍可正常接收输入。
/// </summary>
internal sealed class PopupDismissal : IDisposable
{
    private readonly object sync = new();
    private Dispatcher? inputDispatcher;
    private bool disposed;

    /// <summary>
    /// 在独立消息线程监听低级输入，把按键和点击异步交回调用方的界面线程。
    /// </summary>
    public PopupDismissal(Action<int, int> mouseDown, Action escape)
    {
        var ui = Dispatcher.CurrentDispatcher;
        var thread = new Thread(() => Listen(ui, mouseDown, escape))
        {
            IsBackground = true,
            Name = "SysPulse Popup Input"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// 低级钩子在安装线程上同步接收输入，因此消息循环需要独立于 WPF 布局和刷新。
    /// </summary>
    private void Listen(Dispatcher ui, Action<int, int> mouseDown, Action escape)
    {
        var input = Dispatcher.CurrentDispatcher;
        lock (sync)
        {
            if (disposed)
                return;
            inputDispatcher = input;
        }

        // 回调只复制必要的数据。浮层关闭后，已排队的操作也不能影响下次打开的窗口。
        void Post(Action action) => ui.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (!disposed)
                action();
        });

        InputCallback mouseCallback = (code, message, data) =>
        {
            if (code == 0 && (int)message is 0x0201 or 0x0204 or 0x0207 or 0x020B)
            {
                int x = Marshal.ReadInt32(data), y = Marshal.ReadInt32(data, 4); // MSLLHOOKSTRUCT 的屏幕坐标。
                Post(() => mouseDown(x, y));
            }
            return CallNextHookEx(0, code, message, data);
        };
        InputCallback keyCallback = (code, message, data) =>
        {
            if (code == 0 && (int)message is 0x0100 or 0x0104 && Marshal.ReadInt32(data) == 0x1B)
                Post(escape);
            return CallNextHookEx(0, code, message, data);
        };

        nint module = GetModuleHandle(null);
        nint mouseHook = SetWindowsHookEx(14, mouseCallback, module, 0); // WH_MOUSE_LL
        nint keyHook = SetWindowsHookEx(13, keyCallback, module, 0); // WH_KEYBOARD_LL
        try
        {
            Dispatcher.Run();
        }
        finally
        {
            if (mouseHook != 0) UnhookWindowsHookEx(mouseHook);
            if (keyHook != 0) UnhookWindowsHookEx(keyHook);
            GC.KeepAlive(mouseCallback);
            GC.KeepAlive(keyCallback);
        }
    }

    /// <summary>
    /// 从界面线程取消本次监听，由安装线程退出消息循环并卸载钩子。
    /// </summary>
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            inputDispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        }
    }

    private delegate nint InputCallback(int code, nuint message, nint data);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW")]
    private static extern nint SetWindowsHookEx(int id, InputCallback callback, nint module, uint thread);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MaggotTask.View;

/// <summary>
/// 覆盖层的 Win32 扩展样式工具。
///
/// 全屏透明穿透层必须同时具备：
///   WS_EX_LAYERED     —— AllowsTransparency 的底层要求
///   WS_EX_TRANSPARENT —— 鼠标消息穿透到下层窗口（可运行时开关）
///   WS_EX_TOOLWINDOW  —— 不出现在 Alt-Tab / 任务栏
///   WS_EX_NOACTIVATE  —— 点击不抢焦点
/// </summary>
internal static class WindowStyles
{
    public const int GWL_EXSTYLE = -20;

    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    private static bool Is64Bit => IntPtr.Size == 8;

    public static long GetExStyleRaw(IntPtr hWnd)
        => Is64Bit
            ? GetWindowLongPtr64(hWnd, GWL_EXSTYLE).ToInt64()
            : GetWindowLong32(hWnd, GWL_EXSTYLE);

    private static void SetExStyleRaw(IntPtr hWnd, long value)
    {
        if (Is64Bit)
        {
            SetWindowLongPtr64(hWnd, GWL_EXSTYLE, new IntPtr(value));
        }
        else
        {
            SetWindowLong32(hWnd, GWL_EXSTYLE, unchecked((int)value));
        }
    }

    /// <summary>当前扩展样式（诊断用）。</summary>
    public static int GetExStyle(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return 0;
        return unchecked((int)GetExStyleRaw(hwnd));
    }

    /// <summary>
    /// 应用扩展样式。<paramref name="clickThrough"/> = true 时加上 WS_EX_TRANSPARENT，
    /// 否则清掉它（虫群层开始吃点击，方便「拍」它们）。
    /// </summary>
    /// <returns>应用后的扩展样式位。</returns>
    public static int ApplyOverlayStyles(Window window, bool clickThrough)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("窗口句柄尚未创建：需在 SourceInitialized 之后调用。");
        }

        long style = GetExStyleRaw(hwnd);
        style |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;

        if (clickThrough)
        {
            style |= WS_EX_TRANSPARENT;
        }
        else
        {
            style &= ~(long)WS_EX_TRANSPARENT;
        }

        SetExStyleRaw(hwnd, style);

        // 改完扩展样式要让系统重算窗口框架，否则 WS_EX_TRANSPARENT 不一定立刻生效。
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        return unchecked((int)style);
    }

    /// <summary>是否处于穿透状态。</summary>
    public static bool IsClickThrough(Window window)
        => (GetExStyle(window) & WS_EX_TRANSPARENT) != 0;

    /// <summary>
    /// 面板类窗口：只用 TOOLWINDOW（不进 Alt-Tab），**不**加 NOACTIVATE ——
    /// 控制面板里有文本框和滑块，必须能真正拿到键盘焦点。
    /// </summary>
    public static int ApplyToolWindowStyle(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return 0;

        long style = GetExStyleRaw(hwnd);
        style |= WS_EX_TOOLWINDOW;
        style &= ~(long)WS_EX_NOACTIVATE;
        SetExStyleRaw(hwnd, style);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        return unchecked((int)style);
    }

    /// <summary>
    /// 悬停信息气泡（规格 §14.1 / §14.3）：
    ///   WS_EX_NOACTIVATE  —— 不抢焦点
    ///   WS_EX_TOOLWINDOW  —— 不进 Alt-Tab / 任务栏
    ///   WS_EX_TRANSPARENT —— 气泡下面的桌面照常收到点击（验收标准 5）
    /// </summary>
    public static int ApplyHoverBubbleStyle(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return 0;

        long style = GetExStyleRaw(hwnd);
        style |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
        SetExStyleRaw(hwnd, style);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        return unchecked((int)style);
    }
}

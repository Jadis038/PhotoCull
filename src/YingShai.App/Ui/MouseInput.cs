using System.Runtime.InteropServices;

namespace YingShai.App.Ui;

/// <summary>
/// 真实输入注入：验收脚本用它模拟真正的鼠标点击 / 滚轮（不是设置 SelectedItem 那种"假点击"），
/// 这样能验证"单击缩略图 → 上方查看"和 Photoshop 式滚轮规则的端到端行为。
/// </summary>
public static class MouseInput
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const byte VK_MENU = 0x12;     // Alt
    private const byte VK_CONTROL = 0x11;  // Ctrl

    public static void MoveTo(int x, int y) => SetCursorPos(x, y);

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(60);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(50);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(60);
    }

    public static void DoubleClick(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(80);
        for (int i = 0; i < 2; i++)
        {
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(30);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
            if (i == 0) Thread.Sleep(90);   // 两次点击间隔 < 系统双击时间
        }
        Thread.Sleep(80);
    }

    /// <summary>滚轮：notches 正数向上、负数向下；可带 Alt / Ctrl。</summary>
    public static void Wheel(int x, int y, int notches, bool alt = false, bool ctrl = false)
    {
        SetCursorPos(x, y);
        Thread.Sleep(50);
        if (alt) keybd_event(VK_MENU, 0, 0, IntPtr.Zero);
        if (ctrl) keybd_event(VK_CONTROL, 0, 0, IntPtr.Zero);
        Thread.Sleep(40);
        for (int i = 0; i < Math.Abs(notches); i++)
        {
            mouse_event(MOUSEEVENTF_WHEEL, 0, 0, (uint)(notches > 0 ? 120 : -120), IntPtr.Zero);
            Thread.Sleep(40);
        }
        if (alt) keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        if (ctrl) keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        Thread.Sleep(60);
    }
}

using System.Runtime.InteropServices;
using System.Text;

namespace RightSpeak.Interop;

internal static class WindowFocusInterop
{
    private const int SwRestore = 9;
    private const int ClassNameBufferLength = 256;
    private const int WindowTextBufferLength = 512;
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, nint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        WinEventProc lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    public static bool TryActivateWindow(nint hWnd)
    {
        if (hWnd == nint.Zero)
        {
            return false;
        }

        var foregroundWindow = GetForegroundWindow();
        var foregroundThreadId = foregroundWindow == nint.Zero ? 0 : GetWindowThreadProcessId(foregroundWindow, nint.Zero);
        var currentThreadId = GetCurrentThreadId();
        var attached = false;

        try
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
            }

            if (IsIconic(hWnd))
            {
                ShowWindow(hWnd, SwRestore);
            }

            BringWindowToTop(hWnd);
            return SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
    }

    public static string GetWindowClassName(nint hWnd)
    {
        if (hWnd == nint.Zero)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(ClassNameBufferLength);
        var length = GetClassName(hWnd, builder, builder.Capacity);
        return length > 0 ? builder.ToString() : string.Empty;
    }

    public static string GetWindowText(nint hWnd)
    {
        if (hWnd == nint.Zero)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(WindowTextBufferLength);
        var length = GetWindowText(hWnd, builder, builder.Capacity);
        return length > 0 ? builder.ToString() : string.Empty;
    }

    public static nint SetForegroundChangedHook(WinEventProc callback)
    {
        return SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            nint.Zero,
            callback,
            0,
            0,
            WineventOutOfContext);
    }

    public static bool UnsetForegroundChangedHook(nint hookHandle)
    {
        if (hookHandle == nint.Zero)
        {
            return true;
        }

        return UnhookWinEvent(hookHandle);
    }

    public delegate void WinEventProc(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);
}

using System.Runtime.InteropServices;

namespace RightSpeak.Interop;

internal static class WindowMessageInterop
{
    public static readonly nint HwndBroadcast = new(0xffff);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
}

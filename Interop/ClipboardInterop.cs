using System.Runtime.InteropServices;

namespace RightSpeak.Interop;

internal static class ClipboardInterop
{
    public const byte VkControl = 0x11;
    public const byte VkA = 0x41;
    public const byte VkC = 0x43;
    private const uint KeyEventfKeyUp = 0x0002;

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    public static void SendCopyShortcut()
    {
        keybd_event(VkControl, 0, 0, 0);
        keybd_event(VkC, 0, 0, 0);
        keybd_event(VkC, 0, KeyEventfKeyUp, 0);
        keybd_event(VkControl, 0, KeyEventfKeyUp, 0);
    }

    public static void SendSelectAllShortcut()
    {
        keybd_event(VkControl, 0, 0, 0);
        keybd_event(VkA, 0, 0, 0);
        keybd_event(VkA, 0, KeyEventfKeyUp, 0);
        keybd_event(VkControl, 0, KeyEventfKeyUp, 0);
    }

}

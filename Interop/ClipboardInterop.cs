using System.Runtime.InteropServices;

namespace RightSpeak.Interop;

internal static class ClipboardInterop
{
    public const byte VkControl = 0x11;
    public const byte VkShift = 0x10;
    public const byte VkA = 0x41;
    public const byte VkC = 0x43;
    public const byte VkInsert = 0x2D;
    public const byte VkEscape = 0x1B;
    public const byte VkLeft = 0x25;
    public const byte VkRight = 0x27;
    public const byte VkUp = 0x26;
    public const byte VkDown = 0x28;
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

    public static void SendCopyShortcutCtrlInsert()
    {
        keybd_event(VkControl, 0, 0, 0);
        keybd_event(VkInsert, 0, 0, 0);
        keybd_event(VkInsert, 0, KeyEventfKeyUp, 0);
        keybd_event(VkControl, 0, KeyEventfKeyUp, 0);
    }

    public static void SendEscapeKey()
    {
        keybd_event(VkEscape, 0, 0, 0);
        keybd_event(VkEscape, 0, KeyEventfKeyUp, 0);
    }

    public static void SendLeftArrowKey()
    {
        keybd_event(VkLeft, 0, 0, 0);
        keybd_event(VkLeft, 0, KeyEventfKeyUp, 0);
    }

    public static void SendRightArrowKey()
    {
        keybd_event(VkRight, 0, 0, 0);
        keybd_event(VkRight, 0, KeyEventfKeyUp, 0);
    }

    public static void SendSelectToParagraphStartShortcut()
    {
        keybd_event(VkControl, 0, 0, 0);
        keybd_event(VkShift, 0, 0, 0);
        keybd_event(VkUp, 0, 0, 0);
        keybd_event(VkUp, 0, KeyEventfKeyUp, 0);
        keybd_event(VkShift, 0, KeyEventfKeyUp, 0);
        keybd_event(VkControl, 0, KeyEventfKeyUp, 0);
    }

    public static void SendSelectToParagraphEndShortcut()
    {
        keybd_event(VkControl, 0, 0, 0);
        keybd_event(VkShift, 0, 0, 0);
        keybd_event(VkDown, 0, 0, 0);
        keybd_event(VkDown, 0, KeyEventfKeyUp, 0);
        keybd_event(VkShift, 0, KeyEventfKeyUp, 0);
        keybd_event(VkControl, 0, KeyEventfKeyUp, 0);
    }

}
